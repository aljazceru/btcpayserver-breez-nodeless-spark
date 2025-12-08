#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NBitcoin;
using System;

namespace BTCPayServer.Plugins.Breez
{
    public class BreezPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() { Identifier = nameof(BTCPayServer), Condition = ">=2.2.0" }
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<BreezService>();
            applicationBuilder.AddSingleton<IHostedService>(provider => provider.GetRequiredService<BreezService>());
            applicationBuilder.AddSingleton<BreezLightningConnectionStringHandler>();
            applicationBuilder.AddSingleton<ILightningConnectionStringHandler>(provider => provider.GetRequiredService<BreezLightningConnectionStringHandler>());

            // Register the Breez payment method handler
            applicationBuilder.AddSingleton<IPaymentMethodHandler>(provider =>
            {
                var breezService = provider.GetRequiredService<BreezService>();
                var networkProvider = provider.GetRequiredService<BTCPayNetworkProvider>();
                var lightningClientFactory = provider.GetRequiredService<LightningClientFactoryService>();
                var lightningNetworkOptions = provider.GetRequiredService<IOptions<Configuration.LightningNetworkOptions>>();

                return new BreezPaymentMethodHandler(
                    breezService,
                    networkProvider.GetNetwork<BTCPayNetwork>("BTC"),
                    lightningClientFactory,
                    lightningNetworkOptions);
            });

            // Add UI extensions for lightning setup tab (like Boltz does)
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Breez/LNPaymentMethodSetupTab",
                "ln-payment-method-setup-tab"));
            applicationBuilder.AddSingleton<IUIExtension>(new UIExtension("Breez/LNPaymentMethodSetupTabhead",
                "ln-payment-method-setup-tabhead"));

            // Surface Breez navigation inside the store integrations nav, matching the plugin template pattern.
            applicationBuilder.AddUIExtension("store-integrations-nav", "Breez/BreezNav");

            base.Execute(applicationBuilder);
        }
    }
}
