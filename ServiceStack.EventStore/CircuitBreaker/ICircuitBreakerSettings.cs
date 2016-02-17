﻿namespace ServiceStack.EventStore.CircuitBreaker
{
    using System;

    public interface ICircuitBreakerSettings
    {
        int BreakOnNumberOfExceptions { get; }
        TimeSpan BreakCircuitForSeconds { get; }
    }
}