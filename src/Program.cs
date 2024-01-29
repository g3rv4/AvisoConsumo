using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

var healthChecksClient = new HttpClient { BaseAddress = new Uri("https://hc-ping.com/"), Timeout = TimeSpan.FromSeconds(10) };
var handler = new HttpClientHandler { UseCookies = true };
var client = new HttpClient(handler)
{
    BaseAddress = new Uri("https://brou.e-sistarbanc.com.uy/")
};
var telegramClient = new HttpClient()
{
    BaseAddress =
        new Uri($"https://api.telegram.org/bot{Environment.GetEnvironmentVariable("TELEGRAM_BOT_KEY")}/")
};
client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:109.0) Gecko/20100101 Firefox/114.0");

var numberRegex = new Regex("[0-9]", RegexOptions.Compiled);
var endsInColonRegex = new Regex("[a-z]:$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
do
{
    Console.WriteLine("Getting the token to log in...");
    var r = await client.GetAsync("login/");
    var content = await r.Content.ReadAsStringAsync();

    var doc = new HtmlDocument();
    doc.LoadHtml(content);
    var tks = doc.DocumentNode.SelectNodes("//input[@name='tks']");

    if (tks == null)
    {
        Console.WriteLine("Could not find tks");
        break;
    }

    var tksValue = tks.First().Attributes["value"].Value;

    var values = new Dictionary<string, string>
    {
        { "documento", Environment.GetEnvironmentVariable("DOC") },
        { "password", Environment.GetEnvironmentVariable("PASS") },
        { "tks", tksValue },
        { "button1", "Ingresar" },
    };
    var requestContent = new FormUrlEncodedContent(values);
    Console.WriteLine("Logging in...");
    r = await client.PostAsync("", requestContent);
    content = await r.Content.ReadAsStringAsync();

    doc = new HtmlDocument();
    doc.LoadHtml(content);
    var messagesDivs = doc.DocumentNode.SelectNodes("//div[@class='mensaje info']");
    if (messagesDivs != null)
    {
        var messages = messagesDivs
            .Where(n => !string.IsNullOrWhiteSpace(n.InnerText))
            .Select(n => n.InnerText)
            .ToArray();

        if (messages.Length > 0)
        {
            Console.WriteLine("Could not login. Message: " + messages[0]);
            break;
        }
    }

    values = new Dictionary<string, string>
    {
        { "p3", Environment.GetEnvironmentVariable("CARD_P3") },
    };
    requestContent = new FormUrlEncodedContent(values);

    Console.WriteLine("Getting p3...");
    r = await client.PostAsync("movimientos/", requestContent);
    content = await r.Content.ReadAsStringAsync();

    doc = new HtmlDocument();
    doc.LoadHtml(content);

    var p3 = doc.DocumentNode.SelectNodes("//input[@name='p3']");
    if (p3 == null)
    {
        Console.WriteLine("Could not find p3");
        break;
    }

    var p3Value = p3.First().Attributes["value"].Value;
    while (true)
    {
        Console.WriteLine("Retrieving movements...");
        r = await client.GetAsync("url.php?id=movimientosajax&p3=" + p3Value);
        content = await r.Content.ReadAsStringAsync();

        if (string.IsNullOrWhiteSpace(content))
        {
            Console.WriteLine("Could not get movements. Logging in again");
            break;
        }

        doc = new HtmlDocument();
        doc.LoadHtml(content);

        var movements = doc.DocumentNode.SelectNodes("//div[@class='col-12 cont-movimiento']");
        if (movements != null)
        {
            var pendingMovements = movements.Where(n => n.InnerHtml.Contains("A confirmar")).ToArray();
            if (pendingMovements.Length > 0)
            {
                var alreadyNotified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var currentNotified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var historyOfNotifications = new Dictionary<DateTime, HashSet<string>>();
                var notifications = new List<string>();
                if (File.Exists(Environment.GetEnvironmentVariable("STATE_FILE_PATH")))
                {
                    var fileContentstate = File.ReadAllText(Environment.GetEnvironmentVariable("STATE_FILE_PATH"));
                    historyOfNotifications =
                        JsonSerializer.Deserialize<Dictionary<DateTime, HashSet<string>>>(fileContentstate);
                    alreadyNotified =
                        new HashSet<string>(
                            historyOfNotifications.SelectMany(n => n.Value),
                            StringComparer.OrdinalIgnoreCase);
                }

                foreach (var movement in pendingMovements)
                {
                    var movementDetails = movement.SelectSingleNode(".//div[@class='col-6 ml-2']");
                    var title = movementDetails.SelectSingleNode(".//strong").ChildNodes
                        .Where(n => n.NodeType == HtmlNodeType.Text && !string.IsNullOrWhiteSpace(n.InnerText))
                        .Select(n => n.InnerText.Trim())
                        .Aggregate((a, b) => a + " " + b)
                        .Trim();
                    if (alreadyNotified.Contains(title))
                    {
                        continue;
                    }

                    var desc = movementDetails.ChildNodes
                        .Where(n => n.NodeType == HtmlNodeType.Text && !string.IsNullOrWhiteSpace(n.InnerText))
                        .Select(n => n.InnerText.Trim())
                        .Where(n => !endsInColonRegex.IsMatch(n))
                        .Aggregate((a, b) => a + "\n" + b);

                    var monto = string.Join(" - ", movement.SelectNodes(".//strong[@class='monto_movimiento']")
                        .Select(n => n.InnerText.Trim())
                        .Where(m => numberRegex.IsMatch(m)));

                    notifications.Add(desc + "\nMonto: " + monto);
                    currentNotified.Add(title);
                }

                if (notifications.Count > 0)
                {
                    var msg = new TelegramMessage
                    {
                        chat_id = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"),
                        text = string.Join("\n----------------\n", notifications),
                        disable_web_page_preview = true
                    };
                    var json = JsonSerializer.Serialize(msg);
                    var data = new StringContent(json, Encoding.UTF8, "application/json");
                    r = await telegramClient.PostAsync("sendMessage", data);
                    if (!r.IsSuccessStatusCode)
                    {
                        Console.WriteLine("Could not post message to telegram, got " + r.StatusCode);
                        continue;
                    }

                    if (!historyOfNotifications.ContainsKey(DateTime.UtcNow.Date))
                    {
                        historyOfNotifications[DateTime.UtcNow.Date] =
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }

                    historyOfNotifications[DateTime.UtcNow.Date].UnionWith(currentNotified);

                    foreach (var key in historyOfNotifications.Keys.Where(
                                 k => k < DateTime.UtcNow.Date.AddDays(-15)))
                    {
                        historyOfNotifications.Remove(key);
                    }

                    File.WriteAllText(Environment.GetEnvironmentVariable("STATE_FILE_PATH"),
                        JsonSerializer.Serialize(historyOfNotifications));
                }
            }
        }

        try
        {
            await healthChecksClient.GetAsync("ef58ef4e-9ed7-4b69-ae75-701714d5e7ee");
        }
        catch
        {
            // ignore it, it will trigger an alert if it doesn't get pinged
        }

        Console.WriteLine("Waiting 120s...");
        await Task.Delay(120000);
    }

    Console.WriteLine("Waiting 10s...");
    await Task.Delay(10000);
} while (true);

public class TelegramMessage
{
    public string chat_id { get; set; }
    public string text { get; set; }
    public bool disable_web_page_preview { get; set; }
}