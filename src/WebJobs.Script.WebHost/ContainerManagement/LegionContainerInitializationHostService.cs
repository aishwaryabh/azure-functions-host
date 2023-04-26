﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.WebHost.Management;
using Microsoft.Azure.WebJobs.Script.WebHost.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.WebHost.ContainerManagement
{
    public class LegionContainerInitializationHostService : LinuxContainerInitializationHostService
    {
        private const string ContextFile = "Context.txt";

        private readonly IEnvironment _environment;
        private readonly ILogger _logger;

        public LegionContainerInitializationHostService(IEnvironment environment, IInstanceManager instanceManager,
            ILogger<LinuxContainerInitializationHostService> logger, StartupContextProvider startupContextProvider)
            : base(environment, instanceManager, logger, startupContextProvider)
        {
            _environment = environment;
            _logger = logger;
        }

        public override Task<string> GetStartContextOrNullAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[TEST] GetStartContextOrNullAsyncOnLegion");

            string containerSpecializationContextMountPath = _environment.GetEnvironmentVariable(EnvironmentSettingNames.ContainerSpecializationContextVolumePath);

            _logger.LogInformation($"[TEST] ContainerSpecializationContextMountPath: {containerSpecializationContextMountPath}");

            // The CONTAINER_SPECIALIZATION_CONTEXT_MOUNT_PATH environment variable should be set during pod creation
            if (string.IsNullOrEmpty(containerSpecializationContextMountPath))
            {
                _logger.LogWarning("containerSpecializationContextMountPath is Null or Empty");
                return Task.FromResult<string>(null);
            }

            // The CONTAINER_SPECIALIZATION_CONTEXT_MOUNT_PATH emptyDir volume should be mounted by Legion during pod creation
            if (!Directory.Exists(containerSpecializationContextMountPath))
            {
                _logger.LogWarning("Container Specialization Context Mount Does Not Exist");
                return Task.FromResult<string>(null);
            }

            string contextFilePath = Path.Combine(containerSpecializationContextMountPath, ContextFile);
            _logger.LogInformation($"[TEST] contextFilePath: {contextFilePath}");

            if (File.Exists(contextFilePath))
            {
                _logger.LogInformation($"Previous Start Context Found");
                try
                {
                    string contents = File.ReadAllText(contextFilePath);
                    _logger.LogInformation($"[TEST] contents: {contents}");
                    return Task.FromResult<string>(contents);
                }
                catch (Exception e)
                {
                    _logger.LogError($"Error Reading Previous Start Context: {e.ToString()}");
                }
            }

            return Task.FromResult<string>(null);
        }

        // No-op
        protected override Task SpecializeMSISideCar(HostAssignmentContext assignmentContext)
        {
            return Task.CompletedTask;
        }
    }
}
