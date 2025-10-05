using MtpSimulator.Models;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MtpSimulator
{
    public class SimpleServer : StandardServer
    {
        private readonly ApplicationConfiguration _configuration;
        private readonly IEnumerable<OpcUaItem> _items;
        private OpcNodeManager _nodeManager;

        public SimpleServer(ApplicationConfiguration configuration, IEnumerable<OpcUaItem> items)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _items = items ?? Array.Empty<OpcUaItem>();
        }

        public async Task StartAsync()
        {
            var application = new ApplicationInstance
            {
                ApplicationName = _configuration.ApplicationName ?? "MTP Simulator",
                ApplicationType = ApplicationType.Server,
                ApplicationConfiguration = _configuration
            };

            await application.StartAsync(this).ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            await Task.Run(() => Stop()).ConfigureAwait(false);
        }

        public Task StartSimulationAsync(int intervalMs = 1000)
        {
            _nodeManager?.StartSimulation(intervalMs);
            return Task.CompletedTask;
        }

        public Task StopSimulationAsync()
        {
            _nodeManager?.StopSimulation();
            return Task.CompletedTask;
        }

        protected override MasterNodeManager CreateMasterNodeManager(
            IServerInternal server,
            ApplicationConfiguration configuration)
        {
            _nodeManager = new OpcNodeManager(server, configuration, _items,
                new[] { "urn:mtp:simulator" });

            return new MasterNodeManager(server, configuration, null, new INodeManager[] { _nodeManager });
        }

        protected override ServerProperties LoadServerProperties()
        {
            return new ServerProperties
            {
                ManufacturerName = "MTP Simulator",
                ProductName = "MTP Simulator",
                ProductUri = "urn:mtp:simulator",
                SoftwareVersion = "1.0.0",
                BuildNumber = "1",
                BuildDate = DateTime.UtcNow
            };
        }

        protected override void OnServerStopping()
        {
            try { _nodeManager?.StopSimulation(); } catch { }
            base.OnServerStopping();
        }
    }
}
