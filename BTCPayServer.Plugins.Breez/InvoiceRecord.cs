using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.Breez;

public class InvoiceRecord
{
    public string PaymentHash { get; set; } = string.Empty;
    public string Bolt11 { get; set; } = string.Empty;
    public LightMoney Amount { get; set; } = LightMoney.Zero;
}
