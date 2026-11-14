using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orcub.Net;

namespace Orcub.Daily
{
    public sealed class DailyGrantException : Exception
    {
        public long Status { get; }
        public string Code { get; }
        public bool Permanent => Status >= 400 && Status < 500 && Status != 409 && Status != 408 && Status != 429;
        public DailyGrantException(long status, string code) : base("Daily grant HTTP " + status + ": " + code)
        { Status = status; Code = code; }
    }
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
            if (response.responseCode != 200)
            {
                string code = "";
                try { code = (string)JObject.Parse(response.downloadHandler.text)["error"] ?? ""; }
                catch (Newtonsoft.Json.JsonException) { }
                throw new DailyGrantException(response.responseCode, code);
            }
            var json = JObject.Parse(response.downloadHandler.text);
            if ((string)json["daily"]?["utc_day"] != utcDay || !(json["wallet"] is JObject wallet))
                throw new InvalidOperationException("Daily grant contract unavailable");
            WalletChanged?.Invoke(wallet);
        }
    }
}
