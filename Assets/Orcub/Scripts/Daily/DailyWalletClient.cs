using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orcub.Net;

namespace Orcub.Daily
{
    public sealed class DailyWalletClient : IDailyWallet
    {
        public event Action<JObject> WalletChanged;
        public async Task Grant(string reason, string utcDay, string idempotencyKey)
        {
            var client = ClientServices.Instance;
            if (client == null || !client.Ready) throw new InvalidOperationException("Account not ready");
            using var response = await client.Request("/v1/wallet/grant", "POST", new
            {
                player_id = client.PlayerId, reason, idempotency_key = idempotencyKey,
                ref_id = DailyContext.GrantKey(reason, utcDay), utc_day = utcDay
            });
            if (response.responseCode != 200) throw new InvalidOperationException("Daily grant HTTP " + response.responseCode);
            var json = JObject.Parse(response.downloadHandler.text);
            if ((string)json["daily"]?["utc_day"] != utcDay || !(json["wallet"] is JObject wallet))
                throw new InvalidOperationException("Daily grant contract unavailable");
            WalletChanged?.Invoke(wallet);
        }
    }
}
