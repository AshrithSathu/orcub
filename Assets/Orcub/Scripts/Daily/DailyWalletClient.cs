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
        public bool Permanent => Status==400 || Status==410 || Status==422;
        public DailyGrantException(long status, string code) : base("Daily grant HTTP " + status + ": " + code)
        { Status = status; Code = code; }
    }
    public sealed class DailyWalletClient : IDailyWallet
    {
        readonly string owner;
        internal Func<bool> IsCurrent = () => true;
        public DailyWalletClient() : this(null) { }
        public DailyWalletClient(string playerId) { owner=playerId; }
        public event Action<JObject> WalletChanged;
        public async Task Grant(string reason, string utcDay, string idempotencyKey)
        {
            var client = ClientServices.Instance;
            if (!IsCurrent() || client == null || !client.Ready || client.Stopped || (owner!=null && owner!=client.PlayerId)) throw new InvalidOperationException("Account not ready");
            var player=client.PlayerId;
            using var response = await InstallRetry.Run(()=>client.Request("/v1/wallet/grant", "POST", new
            {
                player_id = client.PlayerId, reason, idempotency_key = idempotencyKey,
                ref_id = DailyContext.GrantKey(reason, utcDay), utc_day = utcDay
            }),r=>r.responseCode,r=>r.Dispose(),client.RenewInstall);
            if(!IsCurrent() || client.Stopped || player!=client.PlayerId) throw new InvalidOperationException("Daily account changed");
            if (response.responseCode != 200)
            {
                string code = "";
                try { code = (string)JObject.Parse(response.downloadHandler.text)["error"] ?? ""; }
                catch (Newtonsoft.Json.JsonException) { }
                throw new DailyGrantException(response.responseCode, code);
            }
            var json = JObject.Load(new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(response.downloadHandler.text)) { DateParseHandling=Newtonsoft.Json.DateParseHandling.None });
            if ((string)json["daily"]?["utc_day"] != utcDay || !(json["wallet"] is JObject wallet))
                throw new InvalidOperationException("Daily grant contract unavailable");
            WalletChanged?.Invoke(wallet);
        }
    }
}
