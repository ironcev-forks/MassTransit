namespace MassTransit.AspNetCoreIntegration.HealthChecks
{
    using System.Collections.Generic;
    using Microsoft.Extensions.Diagnostics.HealthChecks;


    public class HealthCheckOptions
    {
        public string BusHealthCheckName { get; set; }
        public string ReceiveEndpointHealthCheckName { get; set; }
        public HealthStatus FailureStatus { get; set; }
        public IEnumerable<string> Tags { get; set; }

        public static HealthCheckOptions Default =>
            new HealthCheckOptions
            {
                BusHealthCheckName = "masstransit-bus",
                ReceiveEndpointHealthCheckName = "masstransit-endpoint",
                FailureStatus = HealthStatus.Unhealthy,
                Tags = new[] {"ready"}
            };
    }
}
