using System;
using Microsoft.Xrm.Sdk;

namespace RestVtp.Plugins
{
    public abstract class ProviderPluginBase : IPlugin
    {
        /// <summary>Column on the custom data source table holding the mapping JSON.</summary>
        public const string MappingColumn = "rvtp_mappingjson";

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            try
            {
                var cfg = LoadConfig(serviceProvider, trace);
                ExecuteCore(serviceProvider, context, trace, cfg);
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                trace.Trace($"RestVtp: unhandled {ex}");
                throw new InvalidPluginExecutionException(
                    $"RestVtp provider error: {ex.Message}", ex);
            }
        }

        protected abstract void ExecuteCore(
            IServiceProvider serviceProvider,
            IPluginExecutionContext context,
            ITracingService trace,
            MappingConfig cfg);

        private static MappingConfig LoadConfig(IServiceProvider serviceProvider, ITracingService trace)
        {
            // For virtual table data providers, Dataverse supplies the data
            // source record (our "Service Instance") via this service.
            var retriever = (IEntityDataSourceRetrieverService)serviceProvider
                .GetService(typeof(IEntityDataSourceRetrieverService));
            var dataSource = retriever.RetrieveEntityDataSource();

            if (dataSource == null)
                throw new InvalidPluginExecutionException(
                    "RestVtp: no data source record available in context.");

            var json = dataSource.GetAttributeValue<string>(MappingColumn);
            trace.Trace($"RestVtp: loaded mapping config ({json?.Length ?? 0} chars).");
            return MappingConfig.Parse(json);
        }
    }
}
