﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Text.Json;
using WorkerHarness.Core.Matching;
using WorkerHarness.Core.Tests.Helpers;

namespace WorkerHarness.Core.Tests.Matching
{
    [TestClass]
    public class ContextMatcherTests
    {
        [TestMethod]
        [DataRow("A.B")]
        [DataRow("${A}.B")]
        public void Match_InvalidQueryInMatchingContext_ThrowException(string query)
        {
            // Arrange
            MatchingContext context = new()
            {
                Query = query
            };

            object stubObject = new();

            ContextMatcher contextMatcher = new();

            // Act
            try
            {
                contextMatcher.Match(context, stubObject);
            }
            // Assert
            catch (ArgumentException ex)
            {
                StringAssert.Contains(ex.Message, ContextMatcher.MatchingExceptionMessage);
                return;
            }

            Assert.Fail($"The expected {typeof(ArgumentException)} exception is not thrown");
        }

        [TestMethod]
        public void Match_MissingPropertyInSourceObject_ThrowArgumentException()
        {
            // Arrange
            MatchingContext context = new()
            {
                Query = "$.Location.Street"
            };

            object stubObject = WeatherForecast.CreateWeatherForecastObject();

            ContextMatcher contextMatcher = new();

            // Act
            try
            {
                contextMatcher.Match(context, stubObject);
            }
            // Assert
            catch (ArgumentException ex)
            {
                StringAssert.Contains(ex.Message, ContextMatcher.MatchingExceptionMessage);
                return;
            }

            Assert.Fail($"The expected {typeof(ArgumentException)} exception is not thrown");
        }

        [TestMethod]
        public void Match_QueryResultMatchesExpectedValue_ReturnTrue()
        {
            // Arrange
            MatchingContext context = new()
            {
                Query = "$.Location.City",
                Expected = "Redmond"
            };
            context.ConstructExpression();

            object source = WeatherForecast.CreateWeatherForecastObject();

            ContextMatcher contextMatcher = new();

            // Act
            bool actual = contextMatcher.Match(context, source);

            // Assert
            Assert.IsTrue(actual);
        }

        [TestMethod]
        public void Match_QueryResultIsAnObjectThatMatchesExpectedValue_ReturnTrue()
        {
            // Arrange
            object source = WeatherForecast.CreateWeatherForecastObject();

            string expected = JsonSerializer.Serialize(((WeatherForecast)source).Location);

            MatchingContext context = new()
            {
                Query = "$.Location",
                Expected = expected
            };
            context.ConstructExpression();

            ContextMatcher contextMatcher = new();

            // Act
            bool actual = contextMatcher.Match(context, source);

            // Assert
            Assert.IsTrue(actual);
        }

        [TestMethod]
        public void Match_QueryResultFailsToMatchExpectedValue_ReturnFalse()
        {
            // Arrange
            MatchingContext context = new()
            {
                Query = "$.Location.City",
                Expected = "Atlanta"
            };
            context.ConstructExpression();

            object source = WeatherForecast.CreateWeatherForecastObject();

            ContextMatcher contextMatcher = new();

            // Act
            bool actual = contextMatcher.Match(context, source);

            // Assert
            Assert.IsFalse(actual);
        }

        [TestMethod]
        public void MatchAll_SingleMatchThrows_ReturnFalse()
        {
            // Arrange
            MatchingContext context1 = new() { Query = "A.B.C" };
            MatchingContext context2 = new() { Query = "$.B.C" };
            IEnumerable<MatchingContext> contexts = new List<MatchingContext>()
            {
                context1,
                context2
            };
            object stubObject = new();

            ContextMatcher contextMatcher = new();

            // Act
            bool result = contextMatcher.MatchAll(contexts, stubObject);

            // Assert
            Assert.IsFalse(result);
        }

        [TestMethod]
        public void MatchAll_AllContextsSucceeds_ReturnTrue()
        {
            // Arrange
            MatchingContext context1 = new()
            {
                Query = "$.Location.City",
                Expected = "Redmond"
            };
            context1.ConstructExpression();

            MatchingContext context2 = new()
            {
                Query = "$.TemperatureInFahrenheit",
                Expected = "73"
            };
            context2.ConstructExpression();

            IEnumerable<MatchingContext> contexts = new List<MatchingContext>()
            {
                context1,
                context2
            };

            object stubbObject = WeatherForecast.CreateWeatherForecastObject();

            ContextMatcher contextMatcher = new();

            // Act
            bool actual = contextMatcher.MatchAll(contexts, stubbObject);

            // Assert
            Assert.IsTrue(actual);
        }

        [TestMethod]
        public void MatchAll_OneContextFails_ReturnFalse()
        {
            // Arrange
            MatchingContext context1 = new()
            {
                Query = "$.Location.City",
                Expected = "Redmond"
            };
            context1.ConstructExpression();

            MatchingContext context2 = new()
            {
                Query = "$.TemperatureInFahrenheit",
                Expected = "wrong temperature"
            };
            context2.ConstructExpression();

            IEnumerable<MatchingContext> contexts = new List<MatchingContext>()
            {
                context1,
                context2
            };

            object stubbObject = WeatherForecast.CreateWeatherForecastObject();

            ContextMatcher contextMatcher = new();

            // Act
            bool actual = contextMatcher.MatchAll(contexts, stubbObject);

            // Assert
            Assert.IsFalse(actual);
        }
    }
}
