﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host.Config;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Scale;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Configuration;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.WebHost;
using Microsoft.Azure.WebJobs.Script.WebHost.Management;
using Microsoft.Azure.WebJobs.Script.Workers.Http;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.WebJobs.Script.Tests;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using static Microsoft.Azure.WebJobs.Script.Tests.TestHelpers;

namespace Microsoft.Azure.WebJobs.Script.Tests.Managment
{
    public class FunctionsSyncManagerTests : IDisposable
    {
        private const string DefaultTestTaskHub = "TestHubValue";
        private const string DefaultTestConnection = "DurableStorage";
        private const string SyncManagerLogCategory = "Microsoft.Azure.WebJobs.Script.WebHost.Management.FunctionsSyncManager";

        private readonly string _testRootScriptPath;
        private readonly string _testHostConfigFilePath;
        private readonly ScriptApplicationHostOptions _hostOptions;
        private readonly FunctionsSyncManager _functionsSyncManager;
        private readonly Dictionary<string, string> _vars;
        private readonly StringBuilder _contentBuilder;
        private readonly MockHttpHandler _mockHttpHandler;
        private readonly TestLoggerProvider _loggerProvider;
        private readonly ILoggerFactory _loggerFactory;
        private readonly Mock<IScriptWebHostEnvironment> _mockWebHostEnvironment;
        private readonly Mock<IEnvironment> _mockEnvironment;
        private readonly HostNameProvider _hostNameProvider;
        private readonly Mock<ISecretManagerProvider> _secretManagerProviderMock;
        private readonly Mock<ISecretManager> _secretManagerMock;
        private readonly TestScriptHostService _scriptHostManager; // To refresh underlying IConfiguration for IAzureBlobStorageProvider
        private string _function1;
        private bool _emptyContent;
        private bool _secretsEnabled;

        public FunctionsSyncManagerTests()
        {
            _secretsEnabled = true;
            _testRootScriptPath = Path.GetTempPath();
            _testHostConfigFilePath = Path.Combine(_testRootScriptPath, ScriptConstants.HostMetadataFileName);
            FileUtility.DeleteFileSafe(_testHostConfigFilePath);

            _hostOptions = new ScriptApplicationHostOptions
            {
                ScriptPath = Path.Combine("x:", "root"),
                IsSelfHost = false,
                LogPath = Path.Combine("x:", "tmp", "log"),
                SecretsPath = Path.Combine("x:", "secrets"),
                TestDataPath = Path.Combine("x:", "sampledata")
            };

            var jobHostOptions = new ScriptJobHostOptions
            {
                RootScriptPath = _hostOptions.ScriptPath,
                RootLogPath = _hostOptions.LogPath
            };

            string testHostName = "appName.azurewebsites.net";
            _vars = new Dictionary<string, string>
            {
                { EnvironmentSettingNames.WebSiteAuthEncryptionKey, TestHelpers.GenerateKeyHexString() },
                { EnvironmentSettingNames.AzureWebsiteHostName, testHostName }
            };

            ResetMockFileSystem();

            _loggerProvider = new TestLoggerProvider();
            _loggerFactory = new LoggerFactory();
            _loggerFactory.AddProvider(_loggerProvider);
            _contentBuilder = new StringBuilder();
            _mockHttpHandler = new MockHttpHandler(_contentBuilder);
            var httpClientFactory = TestHelpers.CreateHttpClientFactory(_mockHttpHandler);
            var factory = new TestOptionsFactory<ScriptApplicationHostOptions>(_hostOptions);
            var tokenSource = new TestChangeTokenSource<ScriptApplicationHostOptions>();
            var changeTokens = new[] { tokenSource };
            var optionsMonitor = new OptionsMonitor<ScriptApplicationHostOptions>(factory, changeTokens, factory);
            _secretManagerProviderMock = new Mock<ISecretManagerProvider>(MockBehavior.Strict);
            _secretManagerMock = new Mock<ISecretManager>(MockBehavior.Strict);
            _secretManagerProviderMock.SetupGet(p => p.Current).Returns(_secretManagerMock.Object);
            _secretManagerProviderMock.SetupGet(p => p.SecretsEnabled).Returns(() => _secretsEnabled);
            var hostSecretsInfo = new HostSecretsInfo();
            hostSecretsInfo.MasterKey = "aaa";
            hostSecretsInfo.FunctionKeys = new Dictionary<string, string>
                {
                    { "TestHostFunctionKey1", "aaa" },
                    { "TestHostFunctionKey2", "bbb" }
                };
            hostSecretsInfo.SystemKeys = new Dictionary<string, string>
                {
                    { "TestSystemKey1", "aaa" },
                    { "TestSystemKey2", "bbb" }
                };
            _secretManagerMock.Setup(p => p.GetHostSecretsAsync()).ReturnsAsync(hostSecretsInfo);
            Dictionary<string, string> functionSecretsResponse = new Dictionary<string, string>()
                {
                    { "TestFunctionKey1", "aaa" },
                    { "TestFunctionKey2", "bbb" }
                };
            _secretManagerMock.Setup(p => p.GetFunctionSecretsAsync("function1", false)).ReturnsAsync(functionSecretsResponse);
            _secretManagerMock.Setup(p => p.ClearCache());

            var hostIdProviderMock = new Mock<IHostIdProvider>(MockBehavior.Strict);
            hostIdProviderMock.Setup(p => p.GetHostIdAsync(CancellationToken.None)).ReturnsAsync("testhostid123");
            _mockWebHostEnvironment = new Mock<IScriptWebHostEnvironment>(MockBehavior.Strict);
            _mockWebHostEnvironment.SetupGet(p => p.InStandbyMode).Returns(false);
            _mockEnvironment = new Mock<IEnvironment>(MockBehavior.Strict);
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteArmCacheEnabled)).Returns("1");
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteContainerReady)).Returns("1");
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.CoreToolsEnvironment)).Returns((string)null);
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.WebSiteAuthEncryptionKey)).Returns("1");
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteInstanceId)).Returns("1");
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteHostName)).Returns(testHostName);
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.SkipSslValidation)).Returns((string)null);
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebJobsSecretStorageType)).Returns("blob");
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.KubernetesServiceHost)).Returns("");
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.PodNamespace)).Returns("");

            _hostNameProvider = new HostNameProvider(_mockEnvironment.Object);

            var (serviceProvider, services) = CreateDefaultConfigurationWithHostJsonFileAndEnvBuilder();
            var configuration = serviceProvider.GetService<IConfiguration>();

            var functionMetadataProvider = new HostFunctionMetadataProvider(optionsMonitor, NullLogger<HostFunctionMetadataProvider>.Instance, new TestMetricsLogger());
            var functionMetadataManager = TestFunctionMetadataManager.GetFunctionMetadataManager(new OptionsWrapper<ScriptJobHostOptions>(jobHostOptions), functionMetadataProvider, null, new OptionsWrapper<HttpWorkerOptions>(new HttpWorkerOptions()), _loggerFactory, new OptionsWrapper<LanguageWorkerOptions>(CreateLanguageWorkerConfigSettings()));

            _scriptHostManager = new TestScriptHostService(configuration);
            var azureBlobStorageProvider = TestHelpers.GetAzureBlobStorageProvider(configuration, scriptHostManager: _scriptHostManager);

            _functionsSyncManager = new FunctionsSyncManager(configuration, hostIdProviderMock.Object, optionsMonitor, _loggerFactory.CreateLogger<FunctionsSyncManager>(), httpClientFactory, _secretManagerProviderMock.Object, _mockWebHostEnvironment.Object, _mockEnvironment.Object, _hostNameProvider, functionMetadataManager, azureBlobStorageProvider);
            _functionsSyncManager.SetSyncTriggerOptionProvider(new SyncTriggerOptionProvider(serviceProvider, services));
        }

        private Tuple<IServiceProvider, IServiceCollection> CreateDefaultConfigurationWithHostJsonFileAndEnvBuilder()
        {
            var metricsLogger = new Mock<IMetricsLogger>(MockBehavior.Strict);
            metricsLogger.Setup(p => p.BeginEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(new object());
            metricsLogger.Setup(p => p.EndEvent(It.IsAny<Object>()));
            return CreateDefaultConfigurationWithHostJsonFileAndEnvBuilder(_hostOptions, _mockEnvironment.Object, _loggerFactory, metricsLogger.Object);
        }

        private static Tuple<IServiceProvider, IServiceCollection> CreateDefaultConfigurationWithHostJsonFileAndEnvBuilder(ScriptApplicationHostOptions applicationHostOptions, IEnvironment environment, ILoggerFactory loggerFactory, IMetricsLogger metricsLogger)
        {
            var hostBuilder = new HostBuilder();

            IServiceCollection serviceCollection = null;
            hostBuilder.ConfigureAppConfiguration(configurationBuilder =>
            {
                configurationBuilder.AddJsonFile("appsettings.json", optional: true)
                .Add(new HostJsonFileConfigurationSource(applicationHostOptions, environment, loggerFactory, metricsLogger))
                .Add(new ScriptEnvironmentVariablesConfigurationSource());
            });
            hostBuilder.ConfigureServices(services =>
            {
                serviceCollection = services;
            });

            var host = hostBuilder.Build();
            return Tuple.Create(host.Services, serviceCollection);
        }

        private string GetExpectedSyncTriggersPayload(string postedConnection = DefaultTestConnection, string postedTaskHub = DefaultTestTaskHub)
        {
            string taskHubSegment = postedTaskHub != null ? $",\"taskHubName\":\"{postedTaskHub}\"" : "";
            return "[{\"authLevel\":\"anonymous\",\"type\":\"httpTrigger\",\"direction\":\"in\",\"name\":\"req\",\"functionName\":\"function1\"}," +
                $"{{\"name\":\"myQueueItem\",\"type\":\"orchestrationTrigger\",\"direction\":\"in\",\"queueName\":\"myqueue-items\",\"connection\":\"{postedConnection}\",\"functionName\":\"function2\"{taskHubSegment}}}," +
                $"{{\"name\":\"myQueueItem\",\"type\":\"activityTrigger\",\"direction\":\"in\",\"queueName\":\"myqueue-items\",\"connection\":\"{postedConnection}\",\"functionName\":\"function3\"{taskHubSegment}}}]";
        }

        private void ResetConfigurationProvider<T, U>()
            where T : class
            where U : IExtensionConfigProvider
        {
            var (serviceProvider, services) = CreateDefaultConfigurationWithHostJsonFileAndEnvBuilder();

            // TODO: Consider generate KafkaExtensionConfigProvider dyamically.
            var extensionsBuilder = new TestWebJobsExtensionBuilder(
                services,
                ExtensionInfo.FromInstance(Activator.CreateInstance(typeof(U)) as IExtensionConfigProvider));

            extensionsBuilder.BindOptions<T>();
            _functionsSyncManager.SetSyncTriggerOptionProvider(new SyncTriggerOptionProvider(serviceProvider, services));
        }
        internal class TestWebJobsExtensionBuilder : IWebJobsExtensionBuilder
        {
            private readonly IServiceCollection _services;
            private readonly ExtensionInfo _extensionInfo;

            public TestWebJobsExtensionBuilder(IServiceCollection services, ExtensionInfo extentionInfo)
            {
                _services = services;
                _extensionInfo = extentionInfo;
            }

            public IServiceCollection Services => _services;

            public ExtensionInfo ExtensionInfo => _extensionInfo;
        }

        private void ResetMockFileSystem(string hostJsonContent = null, string extensionsJsonContent = null, bool isDurable = true)
        {
            IFileSystem fileSystem = isDurable ? CreateFileSystem(_hostOptions, hostJsonContent, extensionsJsonContent) : CreateFileSystemWithHttpTrigger(_hostOptions, hostJsonContent, extensionsJsonContent);
            FileUtility.Instance = fileSystem;
        }

        [Fact]
        public async Task TrySyncTriggers_StandbyMode_ReturnsFalse()
        {
            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                _mockWebHostEnvironment.SetupGet(p => p.InStandbyMode).Returns(true);
                var result = await _functionsSyncManager.TrySyncTriggersAsync();
                Assert.False(result.Success);
                var expectedMessage = "Invalid environment for SyncTriggers operation.";
                Assert.Equal(expectedMessage, result.Error);
            }
        }

        [Fact]
        public async Task TrySyncTriggers_MaxSyncTriggersPayloadSize_Succeeds()
        {
            // create a dummy file that pushes us over size
            string maxString = new string('x', ScriptConstants.MaxTriggersStringLength + 1);
            _function1 = $"{{ bindings: [], test: '{maxString}'}}";

            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                Assert.True(_functionsSyncManager.ArmCacheEnabled);

                var result = await _functionsSyncManager.TrySyncTriggersAsync();
                Assert.True(result.Success);

                string syncString = _contentBuilder.ToString();
                Assert.True(syncString.Length < ScriptConstants.MaxTriggersStringLength);
                var syncContent = JToken.Parse(syncString);
                Assert.Equal(JTokenType.Array, syncContent.Type);
            }
        }

        [Fact]
        public async Task TrySyncTriggers_LocalEnvironment_ReturnsFalse()
        {
            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.CoreToolsEnvironment)).Returns("1");
                var result = await _functionsSyncManager.TrySyncTriggersAsync();
                Assert.False(result.Success);
                var expectedMessage = "Invalid environment for SyncTriggers operation.";
                Assert.Equal(expectedMessage, result.Error);
            }
        }

        [Fact]
        public void ArmCacheEnabled_VerifyDefault()
        {
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteArmCacheEnabled)).Returns((string)null);
            Assert.True(_functionsSyncManager.ArmCacheEnabled);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task TrySyncTriggers_PostsExpectedContent(bool cacheEnabled)
        {
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteArmCacheEnabled)).Returns(cacheEnabled ? "1" : "0");

            var durableConfig = GetDurableExtensionsSection();
            // HostJsonFileConfigrationProvider validate the host.json format. It requires version section.
            var hostConfig = GetHostConfig(durableConfig: durableConfig, useBundles: false, concurrencyConfig: null, version: "2.0");

            // SyncTriggerPayload extension/concurrency section requires resetConfiguration as true
            SetupDurableExtension(hostConfig.ToString(), durableExtensionJsonVersion: "2.0.0.0", resetConfiguration: true);

            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                Assert.Equal(_functionsSyncManager.ArmCacheEnabled, cacheEnabled);

                // Act
                var syncResult = await _functionsSyncManager.TrySyncTriggersAsync();

                // Assert
                Assert.True(syncResult.Success, "SyncTriggers should return success true");
                Assert.True(string.IsNullOrEmpty(syncResult.Error), "Error should be null or empty");

                // verify expected headers
                Assert.Equal(ScriptConstants.FunctionsUserAgent, _mockHttpHandler.LastRequest.Headers.UserAgent.ToString());
                Assert.True(_mockHttpHandler.LastRequest.Headers.Contains(ScriptConstants.AntaresLogIdHeaderName));

                if (cacheEnabled)
                {
                    VerifyResultWithCacheOn("DurableConnection", "DurableTask", true);
                }
                else
                {
                    VerifyResultWithCacheOff();
                }
            }
        }

        private void VerifyResultWithCacheOn(string connection = DefaultTestConnection, string expectedTaskHub = "TestHubValue", bool inspectExtensionsAndConcurrency = false)
        {
            string expectedSyncTriggersPayload = GetExpectedSyncTriggersPayload(postedConnection: connection, postedTaskHub: expectedTaskHub);
            // verify triggers
            var result = JObject.Parse(_contentBuilder.ToString());
            var triggers = result["triggers"];
            Assert.Equal(expectedSyncTriggersPayload, triggers.ToString(Formatting.None));

            // verify functions
            var functions = (JArray)result["functions"];
            Assert.Equal(3, functions.Count);

            // verify secrets
            var secrets = (JObject)result["secrets"];
            var hostSecrets = (JObject)secrets["host"];
            Assert.Equal("aaa", (string)hostSecrets["master"]);
            var hostFunctionSecrets = (JObject)hostSecrets["function"];
            Assert.Equal("aaa", (string)hostFunctionSecrets["TestHostFunctionKey1"]);
            Assert.Equal("bbb", (string)hostFunctionSecrets["TestHostFunctionKey2"]);
            var systemSecrets = (JObject)hostSecrets["system"];
            Assert.Equal("aaa", (string)systemSecrets["TestSystemKey1"]);
            Assert.Equal("bbb", (string)systemSecrets["TestSystemKey2"]);

            var functionSecrets = (JArray)secrets["function"];
            Assert.Equal(1, functionSecrets.Count);
            var function1Secrets = (JObject)functionSecrets[0];
            Assert.Equal("function1", function1Secrets["name"]);
            Assert.Equal("aaa", (string)function1Secrets["secrets"]["TestFunctionKey1"]);
            Assert.Equal("bbb", (string)function1Secrets["secrets"]["TestFunctionKey2"]);

            if (inspectExtensionsAndConcurrency) {
                // verify extensions 
                var extensions = result["extensions"];
                Assert.Equal(GetExpectedExtensionPayload(connection, expectedTaskHub), extensions.ToString(Formatting.None));

                // verify concurrency
                var concurrency = result["concurrency"];
                Assert.Equal(GetDefaultConcurrencyPayload(), concurrency.ToString(Formatting.None));
            }

            var (logObject, triggersLog) = GetSyncTriggerPayloadLogWithTuple();
            Assert.Equal(expectedSyncTriggersPayload, logObject["triggers"].ToString(Formatting.None));
            Assert.False(triggersLog.Contains("secrets"));
        }

        private void VerifyResultWithCacheOff()
        {
            string expectedSyncTriggersPayload = GetExpectedSyncTriggersPayload("DurableConnection", "DurableTask");
            var result = JObject.Parse(_contentBuilder.ToString());

            // verify triggerrs
            var triggers = result["triggers"];
            Assert.Equal(expectedSyncTriggersPayload, triggers.ToString(Formatting.None));

            // verify extensions 
            var extensions = result["extensions"];
            Assert.Equal(GetExpectedExtensionPayload(), extensions.ToString(Formatting.None));

            // verify concurrency
            var concurrency = result["concurrency"];
            Assert.Equal(GetDefaultConcurrencyPayload(), concurrency.ToString(Formatting.None));

            var (logObject, triggersLog) = GetSyncTriggerPayloadLogWithTuple();
            Assert.Equal(expectedSyncTriggersPayload, logObject["triggers"].ToString(Formatting.None));

            // verify secrets and functions are not exists 
            Assert.False(triggersLog.Contains("secrets"));
            Assert.False(triggersLog.Contains("functions"));
        }

        private void VerifyResultWithSections(string concurrencyPayload = null, string extensionsPayload = null)
        {
            var result = JObject.Parse(_contentBuilder.ToString());
            var concurrency = result["concurrency"];
            var extensions = result["extensions"];

            if (concurrencyPayload != null)
            {
                var concurrencyString = concurrency.ToString(Formatting.None);
                Assert.Equal(concurrencyPayload, concurrencyString);
            } else
            {
                Assert.Null(concurrency);
            }

            if (extensionsPayload != null)
            {
                var extensionsString = extensions.ToString(Formatting.None);
                Assert.Equal(extensionsPayload, extensionsString);
            } else
            {
                Assert.Null(extensions);
            }
        }

        private Tuple<JObject, string> GetSyncTriggerPayloadLogWithTuple()
        {
            var payloadLog = GetSyncTriggerPayloadLog();
            return new (JObject.Parse(payloadLog), payloadLog);
        }

        private JObject GetSyncTriggerPayloadLogWithJObject()
        {
            return JObject.Parse(GetSyncTriggerPayloadLog());
        }

        private string GetSyncTriggerPayloadLog()
        {
            var logs = _loggerProvider.GetAllLogMessages().Where(m => m.Category.Equals(SyncManagerLogCategory)).ToList();

            var log = logs[0];
            int startIdx = log.FormattedMessage.IndexOf("Content=") + 8;
            int endIdx = log.FormattedMessage.LastIndexOf(')');
            return log.FormattedMessage.Substring(startIdx, endIdx - startIdx).Trim();
        }

        [Fact]
        public async Task TrySyncTriggers_SecretsEnabled_ClearsSecretsCache()
        {
            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                var result = await _functionsSyncManager.TrySyncTriggersAsync();
                Assert.True(result.Success);

                _secretManagerMock.Verify(p => p.ClearCache(), Times.Once);
            }
        }

        [Fact]
        public async Task TrySyncTriggers_SecretsDisabled_DoesNotClearSecretsCache()
        {
            _secretsEnabled = false;

            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                var result = await _functionsSyncManager.TrySyncTriggersAsync();
                Assert.True(result.Success);

                _secretManagerMock.Verify(p => p.ClearCache(), Times.Never);
                _secretManagerProviderMock.Verify(p => p.Current, Times.Never);
                _secretManagerProviderMock.Verify(p => p.SecretsEnabled, Times.Exactly(2));
            }
        }

        [Fact]
        public async Task TrySyncTriggers_BackgroundSync_DoesNotPostsEmptyContent()
        {
            _emptyContent = true;

            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                var syncResult = await _functionsSyncManager.TrySyncTriggersAsync(isBackgroundSync: true);
                Assert.Equal(0, _mockHttpHandler.RequestCount);
                Assert.Equal(0, _contentBuilder.Length);
                Assert.True(syncResult.Success);
                Assert.Null(syncResult.Error);

                var logs = _loggerProvider.GetAllLogMessages().Where(m => m.Category.Equals(SyncManagerLogCategory)).Select(p => p.FormattedMessage).ToArray();
                Assert.Equal("No functions found. Skipping Sync operation.", logs.Single());
            }
        }

        [Fact]
        public async Task TrySyncTriggers_BackgroundSync_PostsExpectedContent()
        {
            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                var hashBlob = await _functionsSyncManager.GetHashBlobAsync();
                if (hashBlob != null)
                {
                    await hashBlob.DeleteIfExistsAsync();
                }

                var syncResult = await _functionsSyncManager.TrySyncTriggersAsync(isBackgroundSync: true);
                Assert.True(syncResult.Success);
                Assert.Null(syncResult.Error);
                Assert.Equal(1, _mockHttpHandler.RequestCount);
                var result = JObject.Parse(_contentBuilder.ToString());
                var triggers = result["triggers"];
                Assert.Equal(GetExpectedSyncTriggersPayload(), triggers.ToString(Formatting.None));

                string hash = string.Empty;
                var downloadResponse = await hashBlob.DownloadAsync();
                using (StreamReader reader = new StreamReader(downloadResponse.Value.Content))
                {
                    hash = reader.ReadToEnd();
                }
                Assert.Equal(64, hash.Length);

                // verify log statements
                var logMessages = _loggerProvider.GetAllLogMessages().Where(m => m.Category.Equals(SyncManagerLogCategory)).Select(p => p.FormattedMessage).ToArray();
                Assert.True(logMessages[0].StartsWith("Making SyncTriggers request"));
                var startIdx = logMessages[0].IndexOf("Content=") + 8;
                var endIdx = logMessages[0].LastIndexOf(')');
                var sanitizedContent = logMessages[0].Substring(startIdx, endIdx - startIdx);
                var sanitizedObject = JObject.Parse(sanitizedContent);
                JToken value = null;
                var secretsLogged = sanitizedObject.TryGetValue("secrets", out value);
                Assert.False(secretsLogged);
                Assert.Equal("SyncTriggers call succeeded.", logMessages[1]);
                Assert.Equal($"SyncTriggers hash updated to '{hash}'", logMessages[2]);

                // now sync again - don't expect a sync triggers call this time
                _loggerProvider.ClearAllLogMessages();
                ResetMockFileSystem();
                _mockHttpHandler.Reset();
                syncResult = await _functionsSyncManager.TrySyncTriggersAsync(isBackgroundSync: true);
                Assert.Equal(0, _mockHttpHandler.RequestCount);
                Assert.Equal(0, _contentBuilder.Length);
                Assert.True(syncResult.Success);
                Assert.Null(syncResult.Error);

                logMessages = _loggerProvider.GetAllLogMessages().Where(m => m.Category.Equals(SyncManagerLogCategory)).Select(p => p.FormattedMessage).ToArray();
                Assert.Equal(1, logMessages.Length);
                Assert.Equal($"SyncTriggers hash (Last='{hash}', Current='{hash}')", logMessages[0]);

                // simulate a function change resulting in a new hash value
                ResetMockFileSystem("{}");
                _mockHttpHandler.Reset();
                syncResult = await _functionsSyncManager.TrySyncTriggersAsync(isBackgroundSync: true);
                Assert.Equal(1, _mockHttpHandler.RequestCount);
                Assert.True(syncResult.Success);
                Assert.Null(syncResult.Error);
            }
        }

        [Fact]
        public async Task TrySyncTriggers_BackgroundSync_SetTriggersFailure_HashNotUpdated()
        {
            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                var hashBlob = await _functionsSyncManager.GetHashBlobAsync();
                if (hashBlob != null)
                {
                    await hashBlob.DeleteIfExistsAsync();
                }

                _mockHttpHandler.MockStatusCode = HttpStatusCode.InternalServerError;
                var syncResult = await _functionsSyncManager.TrySyncTriggersAsync(isBackgroundSync: true);
                Assert.False(syncResult.Success);
                string expectedErrorMessage = "SyncTriggers call failed (StatusCode=InternalServerError).";
                Assert.Equal(expectedErrorMessage, syncResult.Error);
                Assert.Equal(1, _mockHttpHandler.RequestCount);
                var result = JObject.Parse(_contentBuilder.ToString());
                var triggers = result["triggers"];
                Assert.Equal(GetExpectedSyncTriggersPayload(), triggers.ToString(Formatting.None));
                bool hashBlobExists = await hashBlob.ExistsAsync();
                Assert.False(hashBlobExists);

                // verify log statements
                var logMessages = _loggerProvider.GetAllLogMessages().Where(m => m.Category.Equals(SyncManagerLogCategory)).Select(p => p.FormattedMessage).ToArray();

                Assert.True(logMessages[0].Contains("Content="));
                Assert.Equal(expectedErrorMessage, logMessages[1]);
            }
        }

        [Fact]
        public async Task TrySyncTriggers_NoDurableTaskHub_UsesBundles_V1DefaultsPosted()
        {
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteName)).Returns("TestHubValue");

            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                var durableConfig = new JObject();

                var hostConfig = GetHostConfig(durableConfig, useBundles: true);

                // See what happens when extension.json is not present but bundles are used.
                SetupDurableExtension(hostConfig.ToString(), durableExtensionJsonVersion: null);

                // Act
                var syncResult = await _functionsSyncManager.TrySyncTriggersAsync();

                // Assert
                Assert.True(syncResult.Success, "SyncTriggers should return success true");
                Assert.True(string.IsNullOrEmpty(syncResult.Error), "Error should be null or empty");

                VerifyResultWithCacheOn(expectedTaskHub: null, connection: null);
            }

        }

        [Fact]
        public async Task TrySyncTriggers_NoDurableTaskHub_DurableV1ExtensionJson_V1DefaultsPosted()
        {
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteName)).Returns("TestHubValue");

            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                var durableConfig = new JObject();

                var hostConfig = GetHostConfig(durableConfig, useBundles: false);

                // See what happens when extension.json is not present but bundles are used.
                SetupDurableExtension(hostConfig.ToString(), durableExtensionJsonVersion: "1.8.3.0");

                // Act
                var syncResult = await _functionsSyncManager.TrySyncTriggersAsync();

                // Assert
                Assert.True(syncResult.Success, "SyncTriggers should return success true");
                Assert.True(string.IsNullOrEmpty(syncResult.Error), "Error should be null or empty");

                VerifyResultWithCacheOn(expectedTaskHub: null, connection: null);
            }
        }

        [Fact]
        public async Task TrySyncTriggers_NoDurableTaskHub_DurableV2ExtensionJson_CleanSiteName_V2DefaultsPosted()
        {
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteName)).Returns("TestHubValue");

            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                var durableConfig = new JObject();

                var hostConfig = GetHostConfig(durableConfig, useBundles: false);

                // See what happens when extension.json is not present but bundles are used.
                SetupDurableExtension(hostConfig.ToString(), durableExtensionJsonVersion: "2.0.0.0");

                // Act
                var syncResult = await _functionsSyncManager.TrySyncTriggersAsync();

                // Assert
                Assert.True(syncResult.Success, "SyncTriggers should return success true");
                Assert.True(string.IsNullOrEmpty(syncResult.Error), "Error should be null or empty");

                VerifyResultWithCacheOn(expectedTaskHub: "TestHubValue", connection: null);
            }
        }


        [Fact]
        public async Task TrySyncTriggers_NoDurableTaskHub_DurableV2ExtensionJson_InvalidSiteName_V2DefaultsPosted()
        {
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteName)).Returns("Test_Hub_Value");

            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                var durableConfig = new JObject();

                var hostConfig = GetHostConfig(durableConfig, useBundles: false);

                // See what happens when extension.json is not present but bundles are used.
                SetupDurableExtension(hostConfig.ToString(), durableExtensionJsonVersion: "2.0.0.0");

                // Act
                var syncResult = await _functionsSyncManager.TrySyncTriggersAsync();

                // Assert
                Assert.True(syncResult.Success, "SyncTriggers should return success true");
                Assert.True(string.IsNullOrEmpty(syncResult.Error), "Error should be null or empty");

                VerifyResultWithCacheOn(expectedTaskHub: "TestHubValue", connection: null);
            }
        }

        [Fact]
        public async Task TrySyncTriggers_DurableV1ExtensionJson_V1ConfigPosted()
        {
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteName)).Returns("TestHubValue");

            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                var durableConfig = new JObject();

                durableConfig["hubName"] = "DurableTask";
                durableConfig["azureStorageConnectionStringName"] = "DurableConnection";

                var hostConfig = GetHostConfig(durableConfig, useBundles: false);

                // See what happens when extension.json is not present but bundles are used.
                SetupDurableExtension(hostConfig.ToString(), durableExtensionJsonVersion: "1.8.3.0");

                // Act
                var syncResult = await _functionsSyncManager.TrySyncTriggersAsync();

                // Assert
                Assert.True(syncResult.Success, "SyncTriggers should return success true");
                Assert.True(string.IsNullOrEmpty(syncResult.Error), "Error should be null or empty");

                VerifyResultWithCacheOn(expectedTaskHub: "DurableTask", connection: "DurableConnection");
            }
        }

        [Fact]
        public async Task TrySyncTriggers_DurableV2ExtensionJson_V2ConfigPosted()
        {
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteName)).Returns("TestHubValue");

            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                var durableConfig = GetDurableExtensionsSection();

                var hostConfig = GetHostConfig(durableConfig, useBundles: false);

                // See what happens when extension.json is not present but bundles are used.
                SetupDurableExtension(hostConfig.ToString(), durableExtensionJsonVersion: "2.0.0.0");

                // Act
                var syncResult = await _functionsSyncManager.TrySyncTriggersAsync();

                // Assert
                Assert.True(syncResult.Success, "SyncTriggers should return success true");
                Assert.True(string.IsNullOrEmpty(syncResult.Error), "Error should be null or empty");

                VerifyResultWithCacheOn(expectedTaskHub: "DurableTask", connection: "DurableConnection");
            }
        }

        [Fact]
        public async Task TrySyncTriggers_Concurrency_And_Extension_Posted()
        {
            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                var concurrencyConifg = GetConcurrencySection();

                // extension section is required mainly for Durable usage. 
                var durableConfig = GetDurableExtensionsSection();

                // HostJsonFileConfigrationProvider validate the host.json format. It requires version section.
                var hostConfig = GetHostConfig(durableConfig: durableConfig, useBundles: false, concurrencyConfig: concurrencyConifg, version: "2.0");

                // SyncTriggerPayload extension/concurrency section requires resetConfiguration as true
                SetupDurableExtension(hostConfig.ToString(), durableExtensionJsonVersion: "2.0.0.0", resetConfiguration: true);

                var syncResult = await _functionsSyncManager.TrySyncTriggersAsync();

                Assert.True(syncResult.Success, "SyncTriggers should return success true");
                Assert.True(string.IsNullOrEmpty(syncResult.Error), "Error should be null or empty");

                VerifyResultWithSections(concurrencyPayload: GetExpectedConcurrencyPayload(), extensionsPayload: GetExpectedExtensionPayload());
            }
        }

        [Fact]
        public async Task TrySyncTriggers_Concurrency_Missing_Posted()
        {
            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                // extension section is required mainly for Durable usage. 
                var durableConfig = GetDurableExtensionsSection();

                // HostJsonFileConfigrationProvider validate the host.json format. It requires version section.
                var hostConfig = GetHostConfig(durableConfig: durableConfig, useBundles: false, version: "2.0");

                // SyncTriggerPayload extension/concurrency section requires resetConfiguration as true
                SetupDurableExtension(hostConfig.ToString(), durableExtensionJsonVersion: "2.0.0.0", resetConfiguration: true);

                var syncResult = await _functionsSyncManager.TrySyncTriggersAsync();

                Assert.True(syncResult.Success, "SyncTriggers should return success true");
                Assert.True(string.IsNullOrEmpty(syncResult.Error), "Error should be null or empty");

                VerifyResultWithSections(concurrencyPayload: GetDefaultConcurrencyPayload(), extensionsPayload: GetExpectedExtensionPayload());
            }
        }

        [Fact]
        public async Task TrySyncTriggers_Extensions_Missing_Posted()
        {
            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                var concurrency = GetConcurrencySection();

                // HostJsonFileConfigrationProvider validate the host.json format. It requires version section.
                var hostConfig = GetHostConfig(durableConfig: null, useBundles: false, version: "2.0", concurrencyConfig: concurrency);

                SetupHttpTrigger(hostConfig.ToString());
                var syncResult = await _functionsSyncManager.TrySyncTriggersAsync();

                Assert.True(syncResult.Success, "SyncTriggers should return success true");
                Assert.True(string.IsNullOrEmpty(syncResult.Error), "Error should be null or empty");

                VerifyResultWithSections(concurrencyPayload: GetExpectedConcurrencyPayload(), extensionsPayload: GetExpectedDefaultHttpExtensionPayload());
            }
        }

        private JObject GetDurableExtensionsSection()
        {
            var durableConfig = new JObject();

            durableConfig["hubName"] = "DurableTask";

            var azureStorageConfig = new JObject();
            azureStorageConfig["connectionStringName"] = "DurableConnection";
            durableConfig["storageProvider"] = azureStorageConfig;
            return durableConfig;
        }

        private JObject GetConcurrencySection()
        {
            var concurrencyConifg = new JObject();
            concurrencyConifg["dynamicConcurrencyEnabled"] = true;
            concurrencyConifg["snapshotPersistenceEnabled"] = true;
            return concurrencyConifg;
        }



        private string GetExpectedConcurrencyPayload()
        {
            return "{\"dynamicConcurrencyEnabled\":true,\"maximumFunctionConcurrency\":500,\"cpuThreshold\":0.8,\"snapshotPersistenceEnabled\":true}";
        }

        private string GetDefaultConcurrencyPayload()
        {
            return "{\"dynamicConcurrencyEnabled\":false,\"maximumFunctionConcurrency\":500,\"cpuThreshold\":0.8,\"snapshotPersistenceEnabled\":true}";
        }

        private string GetExpectedExtensionPayload(string connection = "DurableConnection", string taskHubName = "DurableTask")
        {
             return $"{{\"durableTask\":{{\"httpSettings\":{{\"defaultAsyncRequestSleepTimeMilliseconds\":30000}},\"hubName\":\"{taskHubName}\",\"storageProvider\":{{\"connectionStringName\":\"{connection}\"}},\"tracing\":{{\"traceInputsAndOutputs\":false,\"allowVerboseLinuxTelemetry\":false,\"traceReplayEvents\":false,\"distributedTracingEnabled\":false,\"distributedTracingProtocol\":\"HttpCorrelationProtocol\"}},\"notifications\":{{\"eventGrid\":null}},\"maxConcurrentActivityFunctions\":null,\"maxConcurrentOrchestratorFunctions\":null,\"localRpcEndpointEnabled\":null,\"extendedSessionsEnabled\":false,\"extendedSessionIdleTimeoutInSeconds\":30,\"maxOrchestrationActions\":100000,\"overridableExistingInstanceStates\":1,\"entityMessageReorderWindowInMinutes\":30,\"useGracefulShutdown\":false,\"rollbackEntityOperationsOnExceptions\":true,\"useAppLease\":true,\"appLeaseOptions\":{{\"renewInterval\":\"00:00:25\",\"acquireInterval\":\"00:05:00\",\"leaseInterval\":\"00:01:00\"}}}}}}";
        }

        private string GetExpectedDefaultHttpExtensionPayload()
        {
            return "{\"http\":{\"dynamicThrottlesEnabled\":false,\"enableChunkedRequestBinding\":false,\"maxConcurrentRequests\":-1,\"maxOutstandingRequests\":-1,\"routePrefix\":\"api\"}}";
        }

        [Fact]
        public async Task UpdateHashAsync_Succeeds()
        {
            var hashBlobClient = await _functionsSyncManager.GetHashBlobAsync();
            await hashBlobClient.DeleteIfExistsAsync();

            // add the hash when it doesn't exist
            await _functionsSyncManager.UpdateHashAsync(hashBlobClient, "hash1");

            // read the hash and make sure the value is what we wrote
            string result;
            var downloadResponse = await hashBlobClient.DownloadAsync();
            using (StreamReader reader = new StreamReader(downloadResponse.Value.Content))
            {
                result = reader.ReadToEnd();
            }
            Assert.Equal("hash1", result);

            // now update the existing hash to a new value
            await _functionsSyncManager.UpdateHashAsync(hashBlobClient, "hash2");

            downloadResponse = await hashBlobClient.DownloadAsync();
            using (StreamReader reader = new StreamReader(downloadResponse.Value.Content))
            {
                result = reader.ReadToEnd();
            }
            Assert.Equal("hash2", result);
        }

        private void VerifyLoggedInvalidOperationException(string errorMessage)
        {
            Exception[] messages = _loggerProvider.GetAllLogMessages().Where(m => m.Category.Equals(SyncManagerLogCategory)).Where(p => p.Level == LogLevel.Error).Select(p => p.Exception).ToArray();
            Assert.Equal(1, messages.Length);
            Assert.True(messages[0] is InvalidOperationException);
            Assert.Equal(errorMessage, messages[0].Message);
        }

        private void SetupDurableExtension(string hostJson, string durableExtensionJsonVersion, bool resetConfiguration = false)
        {
            string extensionJson = durableExtensionJsonVersion != null ? GetExtensionsJson(durableExtensionJsonVersion) : null;
            ResetMockFileSystem(hostJsonContent: hostJson, extensionsJsonContent: extensionJson);
            if (resetConfiguration)
            {
                // This operation update the SyncTriggerConfigProvider Setup of FunctionSyncManager.
                // If you change the host.json payload and want to test extensions/concurrency section on the SyncTrigger payload
                // You need to update the Mock behavior by the latest FileUtils settings.
                ResetConfigurationProvider<DurableTaskOptions, DurableTaskExtensionConfigProvider>();
            }
        }

        // This is just for testing. TODO we should create this dynamically
        // Actual Durable implementation have several providers.
        private class DurableTaskExtensionConfigProvider : IExtensionConfigProvider
        {
            public void Initialize(ExtensionConfigContext context)
            {
                throw new NotImplementedException();
            }
        }


        private void SetupHttpTrigger(string hostJson)
        {
            ResetMockFileSystem(hostJsonContent: hostJson);
            ResetConfigurationProvider<Microsoft.Azure.WebJobs.Extensions.Http.HttpOptions, HttpExtensionConfigProvider>();
        }

        // This is just for testing. TODO we should create this dynamically
        private class HttpExtensionConfigProvider : IExtensionConfigProvider
        {
            public void Initialize(ExtensionConfigContext context)
            {
                throw new NotImplementedException();
            }
        }

        private JObject GetHostConfig(JObject durableConfig, bool useBundles = false, JObject concurrencyConfig = null, string version = null)
        {
            var extensionsConfig = new JObject();

            if (durableConfig != null)
            {
                extensionsConfig.Add("durableTask", durableConfig);
            }

            var hostConfig = new JObject();

            if (durableConfig != null)
            {
                hostConfig.Add("extensions", extensionsConfig);
            }

            if (version != null)
            {
                hostConfig["version"] = version;
            }

            if (useBundles)
            {
                var extensionBundleConfig = new JObject();
                extensionBundleConfig["id"] = "Microsoft.Azure.Functions.ExtensionBundle";
                extensionBundleConfig["version"] = "[2.*, 3.0.0)";
                hostConfig["extensionBundle"] = extensionBundleConfig;
            }

            if (concurrencyConfig != null)
            {
                hostConfig["concurrency"] = concurrencyConfig;
            }

            return hostConfig;
        }

        private string GetExtensionsJson(string durableVersion)
        {
            var durableExtension = new JObject();
            durableExtension["name"] = "DurableTask";
            durableExtension["typeName"] = $"Microsoft.Azure.WebJobs.Extensions.DurableTask.DurableTaskWebJobsStartup, Microsoft.Azure.WebJobs.Extensions.DurableTask, Version={durableVersion}, Culture=neutral, PublicKeyToken=014045d636e89289";
            var extensions = new JArray();
            extensions.Add(durableExtension);
            var extensionJson = new JObject();
            extensionJson["extensions"] = extensions;
            return extensionJson.ToString();
        }

        [Theory]
        [InlineData("")]
        [InlineData("notaconnectionstring")]
        public async Task CheckAndUpdateHashAsync_ReturnsExpectedValue(string storageConnectionString)
        {
            _vars.Add("AzureWebJobsStorage", storageConnectionString);
            using (var env = new TestScopedEnvironmentVariable(_vars))
            {
                // _functionsSyncManager is initialized in the constructor with all the secrets from environment,
                // so HostAzureBlobStorageProvider will have AzureWebJobsStorage defined in both ActiveHostConfigurationSource
                // and the WebHost IConfiguration source from DI.
                // The TestScopedEnvironmentVariable only changes the WebHost level IConfiguration (so if it is overriden to
                // "notaconnectionstring" the test behaves as expected.
                // When it is set to empty, the connection string from the ActiveHostConfigurationSource wins (never changed since it is set in
                // constructor as mentioned).
                // Therefore, we need to force refresh the configuration with an ActiveHostChanged event. This is because setting an empty environment variable
                // removes it, but will not remove it from the ActiveHostConfigurationSource.
                _scriptHostManager.OnActiveHostChanged();
                var blob = await _functionsSyncManager.GetHashBlobAsync();
                Assert.Null(blob);
            }
        }

        [Theory]
        [InlineData(1, "http://sitename/operations/settriggers")]
        [InlineData(0, "https://sitename/operations/settriggers")]
        public void Disables_Ssl_If_SkipSslValidation_Enabled(int skipSslValidation, string syncTriggersUri)
        {
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.SkipSslValidation)).Returns(skipSslValidation.ToString());
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteHostName)).Returns("sitename");

            var httpRequest = _functionsSyncManager.BuildSetTriggersRequest();
            Assert.Equal(syncTriggersUri, httpRequest.RequestUri.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, httpRequest.Method);
        }

        [Theory]
        [InlineData(null, "sitename", "https://sitename.azurewebsites.net/operations/settriggers")]
        [InlineData("hostname", null, "https://hostname/operations/settriggers")]
        public void Use_Website_Name_If_Website_Hostname_Is_Not_Available(string hostName, string siteName, string expectedSyncTriggersUri)
        {
            _hostNameProvider.Reset();
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteName)).Returns(siteName);
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteHostName)).Returns(hostName);

            var httpRequest = _functionsSyncManager.BuildSetTriggersRequest();
            Assert.Equal(expectedSyncTriggersUri, httpRequest.RequestUri.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, httpRequest.Method);
        }

        [Theory]
        [InlineData("KUBERNETES_SERVICE_HOST", "http://k8se-build-service.k8se-system.svc.cluster.local:8181/api/operations/settriggers")]
        [InlineData(null, "https://appname.azurewebsites.net/operations/settriggers")]
        public void Managed_Kubernetes_Environment_SyncTrigger_Url_Validation(string kubernetesServiceHost, string expectedSyncTriggersUri)
        {
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.KubernetesServiceHost)).Returns(kubernetesServiceHost);
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable(EnvironmentSettingNames.PodNamespace)).Returns("POD_NAMESPACE");
            _mockEnvironment.Setup(p => p.GetEnvironmentVariable("BUILD_SERVICE_HOSTNAME"))
                .Returns("");
            var httpRequest = _functionsSyncManager.BuildSetTriggersRequest();
            Assert.Equal(expectedSyncTriggersUri, httpRequest.RequestUri.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, httpRequest.Method);
        }

        private static LanguageWorkerOptions CreateLanguageWorkerConfigSettings()
        {
            return new LanguageWorkerOptions
            {
                WorkerConfigs = TestHelpers.GetTestWorkerConfigs()
            };
        }

        private IFileSystem CreateFileSystem(ScriptApplicationHostOptions hostOptions, string hostJsonContent = null, string extensionsJsonContent = null)
        {
            var rootPath = hostOptions.ScriptPath;
            string testDataPath = hostOptions.TestDataPath;

            var fullFileSystem = new FileSystem();
            var fileSystem = new Mock<IFileSystem>();
            var fileBase = new Mock<FileBase>();
            var dirBase = new Mock<DirectoryBase>();

            fileSystem.SetupGet(f => f.Path).Returns(fullFileSystem.Path);
            fileSystem.SetupGet(f => f.File).Returns(fileBase.Object);

            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, "host.json"))).Returns(true);

            var durableConfig = new JObject
            {
                { "HubName", "TestHubValue" },
                { "azureStorageConnectionStringName", "DurableStorage" }
            };
            var extensionsConfig = new JObject
            {
                { "durableTask", durableConfig }
            };
            var defaultHostConfig = new JObject
            {
                { "version", "2.0" },
                { "extensions", extensionsConfig }
            };
            hostJsonContent = hostJsonContent ?? defaultHostConfig.ToString();
            fileBase.Setup(f => f.Open(Path.Combine(rootPath, @"host.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                hostJsonContent = hostJsonContent ?? defaultHostConfig.ToString();
                var testHostJsonStream = new MemoryStream(Encoding.UTF8.GetBytes(hostJsonContent));
                testHostJsonStream.Position = 0;
                return testHostJsonStream;
            });

            fileBase.Setup(f => f.ReadAllText(Path.Combine(rootPath, @"host.json"))).Returns(() =>
            {
                return hostJsonContent ?? defaultHostConfig.ToString();
            });

            fileBase.Setup(f => f.WriteAllText(Path.Combine(rootPath, @"host.json"), It.IsAny<string>())).Callback((string path, string contents) =>
            {
                // If you need to validate WriteAll, write logic in here.
            });

            if (extensionsJsonContent != null)
            {
                string extensionJsonPath = Path.Combine(Path.Combine(rootPath, "bin"), "extensions.json");
                fileBase.Setup(f => f.Exists(extensionJsonPath)).Returns(true);
                var testExtensionsJsonStream = new MemoryStream(Encoding.UTF8.GetBytes(extensionsJsonContent));
                testExtensionsJsonStream.Position = 0;
                fileBase.Setup(f => f.Open(extensionJsonPath, It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(testExtensionsJsonStream);
            }

            fileSystem.SetupGet(f => f.Directory).Returns(dirBase.Object);
            dirBase.Setup(d => d.Exists(rootPath)).Returns(true);
            dirBase.Setup(d => d.Exists(Path.Combine(rootPath, "bin"))).Returns(true);
            dirBase.Setup(d => d.Exists(Path.Combine(rootPath, @"function1"))).Returns(true);
            dirBase.Setup(d => d.Exists(Path.Combine(rootPath, @"function2"))).Returns(true);
            dirBase.Setup(d => d.Exists(Path.Combine(rootPath, @"function3"))).Returns(true);
            dirBase.Setup(d => d.EnumerateDirectories(rootPath))
                .Returns(() =>
                {
                    if (_emptyContent)
                    {
                        return new string[0];
                    }
                    else
                    {
                        return new[]
                        {
                            Path.Combine(rootPath, "bin"),
                            Path.Combine(rootPath, "function2"),
                            Path.Combine(rootPath, "function1"),
                            Path.Combine(rootPath, "function3")
                        };
                    }
                });

            _function1 = @"{
  ""scriptFile"": ""main.py"",
  ""disabled"": false,
  ""bindings"": [
    {
      ""authLevel"": ""anonymous"",
      ""type"": ""httpTrigger"",
      ""direction"": ""in"",
      ""name"": ""req""
    },
    {
      ""type"": ""http"",
      ""direction"": ""out"",
      ""name"": ""$return""
    }
  ]
}";
            var function2 = @"{
  ""disabled"": false,
  ""scriptFile"": ""main.js"",
  ""bindings"": [
    {
      ""name"": ""myQueueItem"",
      ""type"": ""orchestrationTrigger"",
      ""direction"": ""in"",
      ""queueName"": ""myqueue-items"",
      ""connection"": """"
    }
  ]
}";

            var function3 = @"{
  ""disabled"": false,
  ""scriptFile"": ""main.js"",
  ""bindings"": [
    {
      ""name"": ""myQueueItem"",
      ""type"": ""activityTrigger"",
      ""direction"": ""in"",
      ""queueName"": ""myqueue-items"",
      ""connection"": """"
    }
  ]
}";
            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function1\function.json"))).Returns(true);
            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function1\main.py"))).Returns(true);
            fileBase.Setup(f => f.ReadAllText(Path.Combine(rootPath, @"function1\function.json"))).Returns(_function1);
            fileBase.Setup(f => f.Open(Path.Combine(rootPath, @"function1\function.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(_function1));
            });
            fileBase.Setup(f => f.Open(Path.Combine(testDataPath, "function1.dat"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(_function1));
            });

            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function2\function.json"))).Returns(true);
            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function2\main.js"))).Returns(true);
            fileBase.Setup(f => f.ReadAllText(Path.Combine(rootPath, @"function2\function.json"))).Returns(function2);
            fileBase.Setup(f => f.Open(Path.Combine(rootPath, @"function2\function.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(function2));
            });
            fileBase.Setup(f => f.Open(Path.Combine(testDataPath, "function2.dat"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(_function1));
            });

            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function3\function.json"))).Returns(true);
            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function3\main.js"))).Returns(true);
            fileBase.Setup(f => f.ReadAllText(Path.Combine(rootPath, @"function3\function.json"))).Returns(function3);
            fileBase.Setup(f => f.Open(Path.Combine(rootPath, @"function3\function.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(function3));
            });
            fileBase.Setup(f => f.Open(Path.Combine(testDataPath, "function3.dat"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(_function1));
            });

            return fileSystem.Object;
        }

        private IFileSystem CreateFileSystemWithHttpTrigger(ScriptApplicationHostOptions hostOptions, string hostJsonContent = null, string extensionsJsonContent = null)
        {
            var rootPath = hostOptions.ScriptPath;
            string testDataPath = hostOptions.TestDataPath;

            var fullFileSystem = new FileSystem();
            var fileSystem = new Mock<IFileSystem>();
            var fileBase = new Mock<FileBase>();
            var dirBase = new Mock<DirectoryBase>();

            fileSystem.SetupGet(f => f.Path).Returns(fullFileSystem.Path);
            fileSystem.SetupGet(f => f.File).Returns(fileBase.Object);

            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, "host.json"))).Returns(true);

            var defaultHostConfig = new JObject
            {
                { "version", "2.0" },
            };
            hostJsonContent = hostJsonContent ?? defaultHostConfig.ToString();
            fileBase.Setup(f => f.Open(Path.Combine(rootPath, @"host.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                hostJsonContent = hostJsonContent ?? defaultHostConfig.ToString();
                var testHostJsonStream = new MemoryStream(Encoding.UTF8.GetBytes(hostJsonContent));
                testHostJsonStream.Position = 0;
                return testHostJsonStream;
            });

            fileBase.Setup(f => f.ReadAllText(Path.Combine(rootPath, @"host.json"))).Returns(() =>
            {
                return hostJsonContent ?? defaultHostConfig.ToString();
            });

            fileBase.Setup(f => f.WriteAllText(Path.Combine(rootPath, @"host.json"), It.IsAny<string>())).Callback((string path, string contents) =>
            {
                // If you need to validate WriteAll, write logic in here.
            });

            if (extensionsJsonContent != null)
            {
                string extensionJsonPath = Path.Combine(Path.Combine(rootPath, "bin"), "extensions.json");
                fileBase.Setup(f => f.Exists(extensionJsonPath)).Returns(true);
                var testExtensionsJsonStream = new MemoryStream(Encoding.UTF8.GetBytes(extensionsJsonContent));
                testExtensionsJsonStream.Position = 0;
                fileBase.Setup(f => f.Open(extensionJsonPath, It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(testExtensionsJsonStream);
            }

            fileSystem.SetupGet(f => f.Directory).Returns(dirBase.Object);
            dirBase.Setup(d => d.Exists(rootPath)).Returns(true);
            dirBase.Setup(d => d.Exists(Path.Combine(rootPath, "bin"))).Returns(true);
            dirBase.Setup(d => d.Exists(Path.Combine(rootPath, @"function1"))).Returns(true);
            dirBase.Setup(d => d.EnumerateDirectories(rootPath))
                .Returns(() =>
                {
                    if (_emptyContent)
                    {
                        return new string[0];
                    }
                    else
                    {
                        return new[]
                        {
                            Path.Combine(rootPath, "bin"),
                            Path.Combine(rootPath, "function1"),
                        };
                    }
                });

            _function1 = @"{
  ""scriptFile"": ""main.py"",
  ""disabled"": false,
  ""bindings"": [
    {
      ""authLevel"": ""anonymous"",
      ""type"": ""httpTrigger"",
      ""direction"": ""in"",
      ""name"": ""req""
    },
    {
      ""type"": ""http"",
      ""direction"": ""out"",
      ""name"": ""$return""
    }
  ]
}";
 
            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function1\function.json"))).Returns(true);
            fileBase.Setup(f => f.Exists(Path.Combine(rootPath, @"function1\main.py"))).Returns(true);
            fileBase.Setup(f => f.ReadAllText(Path.Combine(rootPath, @"function1\function.json"))).Returns(_function1);
            fileBase.Setup(f => f.Open(Path.Combine(rootPath, @"function1\function.json"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(_function1));
            });
            fileBase.Setup(f => f.Open(Path.Combine(testDataPath, "function1.dat"), It.IsAny<FileMode>(), It.IsAny<FileAccess>(), It.IsAny<FileShare>())).Returns(() =>
            {
                return new MemoryStream(Encoding.UTF8.GetBytes(_function1));
            });

            return fileSystem.Object;
        }

        public void Dispose()
        {
            // Clean up mock IFileSystem
            FileUtility.Instance = null;
            Environment.SetEnvironmentVariable(EnvironmentSettingNames.WebSiteAuthEncryptionKey, string.Empty);
            Environment.SetEnvironmentVariable("WEBSITE_SITE_NAME", string.Empty);
            FileUtility.DeleteFileSafe(_testHostConfigFilePath);
        }

        private class MockHttpHandler : HttpClientHandler
        {
            private StringBuilder _content;

            public MockHttpHandler(StringBuilder content)
            {
                _content = content;
                MockStatusCode = HttpStatusCode.OK;
            }

            public int RequestCount { get; set; }

            public HttpStatusCode MockStatusCode { get; set; }

            public HttpRequestMessage LastRequest { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                RequestCount++;
                _content.Append(await request.Content.ReadAsStringAsync());
                return new HttpResponseMessage(MockStatusCode);
            }

            public void Reset()
            {
                LastRequest = null;
                _content.Clear();
                RequestCount = 0;
            }
        }
    }
}