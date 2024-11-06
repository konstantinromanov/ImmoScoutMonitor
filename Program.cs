using HtmlAgilityPack;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Diagnostics;


class Program
{
    // ------------------------ settings -----------------------------
    private static double intervalInMintes = 3;
    private static string city = "Bern";
    private static int searchRadiusInMeters = 5;
    private static int priceTo = 1300;
    private static int priceFrom = 0;
    // ---------------------------------------------------------------

    private static List<string> previousItems = new List<string>();
    private static string baseUrl = "https://www.immoscout24.ch";
    private static string dataFilePath = Path.Combine(Environment.CurrentDirectory, "NewItemsHistory");
    private static string dataFileName = "items.json";
    private static bool isUrlOpen = false;

    static async Task Main(string[] args)
    {
        Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss")} - Program start.");

        // very first run of the program
        if (!File.Exists(Path.Combine(dataFilePath, dataFileName)))
        {
            Directory.CreateDirectory(dataFilePath);
            Console.WriteLine($"Created folder: {dataFilePath}");

            string content = await GetItemsFromUrl();

            var newItemsIds = ParseItemIdsFromHtml(content);
            Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss")} - This is the first run, creating history file.");

            List<Item> items = new List<Item>();
            newItemsIds.ForEach(itemId => items.Add(GetItemParams(content, itemId)));

            await SaveCurrentItemsAsync(items);
        }

        string jsonFromFile = File.ReadAllText(Path.Combine(dataFilePath, dataFileName));
        List<Item> existingItems = JsonConvert.DeserializeObject<List<Item>>(jsonFromFile) ?? new List<Item>();

        existingItems.ForEach(item => previousItems.Add(item.Id));
        Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss")} - History restored from the file.");

        while (true)
        {
            Thread.Sleep(TimeSpan.FromMinutes(intervalInMintes));

            string content = await GetItemsFromUrl();

            await CheckForNewInserts(content);
        }
    }

    private static async Task<string> GetItemsFromUrl()
    {
        string url = $"{baseUrl}/de/immobilien/mieten/ort-{city.ToLower()}?nrt={searchRadiusInMeters}&o=dateCreated-desc&pf={priceFrom}&pt={priceTo}";
        string content = await GetHtmlContent(url);

        if (content == null)
        {
            Console.WriteLine("Failed to retrieve content.");

            return String.Empty;
        }

        return content;
    }

    private static async Task CheckForNewInserts(string content)
    {        
        List<string> currentItemsIds = ParseItemIdsFromHtml(content);
        List<string> newInsertsIds = currentItemsIds.Except(previousItems).ToList();       
        List<Item> newItems = new List<Item>();

        if (newInsertsIds.Count > 0)
        {
            Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss")} - New inserts detected:");

            foreach (var newItem in newInsertsIds)
            {
                Console.WriteLine($"ID: {newItem}");                
                Item item = GetItemParams(content, newItem);

                if (item.Address != null && item.Url != null)
                {
                    await ShowToastNotification(item);

                    Console.WriteLine($"Title: {item.Title}");
                    Console.WriteLine($"Address: {item.Address}");  
                    Console.WriteLine($"Full URL: {item.Url}");

                    newItems.Add(item);
                }
            }

            await SaveCurrentItemsAsync(newItems);
            previousItems.AddRange(newInsertsIds);
        }
        else
        {
            Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss")} - No new inserts detected.");
        }
    }

    private static async Task<string> GetHtmlContent(string url)
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Host = "www.immoscout24.ch";
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:131.0) Gecko/20100101 Firefox/131.0");
                client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/png,image/svg+xml,*/*;q=0.8");
                client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br, zstd");
                client.DefaultRequestHeaders.Add("Connection", "keep-alive");

                var response = await client.GetAsync(url);

                response.EnsureSuccessStatusCode();

                var contentStream = await response.Content.ReadAsStreamAsync();
                string content;

                // Decompress the stream if the content encoding is gzip
                if (response.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    using (var decompressedStream = new System.IO.Compression.GZipStream(contentStream, System.IO.Compression.CompressionMode.Decompress))
                    using (var reader = new StreamReader(decompressedStream))
                    {
                        content = await reader.ReadToEndAsync();
                    }
                }
                else
                {
                    // If no compression, just read the content directly
                    using (var reader = new StreamReader(contentStream))
                    {
                        content = await reader.ReadToEndAsync();
                    }
                }

                return content;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss")} - Error fetching HTML content: " + ex.Message);
        }

        return String.Empty;
    }

    private static List<string> ParseItemIdsFromHtml(string htmlContent)
    {
        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(htmlContent);

        var scriptNode = htmlDoc.DocumentNode
            .SelectNodes("//script")
            .FirstOrDefault(node => node.InnerText.Contains("window.dataLayer.push"));

        if (scriptNode != null)
        {
            string scriptContent = scriptNode.InnerText;
            List<string> itemIds = ExtractItemIds(scriptContent);

            return itemIds;
        }

        return new List<string>();
    }

    private static List<string> ExtractItemIds(string scriptContent)
    {
        List<string>? itemIds = new List<string>();
        int startIndex = scriptContent.IndexOf("\"h_srp_items_loaded_078\": [");
        int jsonStart = scriptContent.IndexOf('{');
        int jsonEnd = scriptContent.LastIndexOf('}');

        if (jsonStart != -1 && jsonEnd != -1)
        {
            string jsonData = scriptContent.Substring(jsonStart, jsonEnd - jsonStart + 1);
            JObject? dataObject = JsonConvert.DeserializeObject<JObject>(jsonData);

            if (dataObject != null && dataObject.ContainsKey("h_srp_items_loaded_078"))
            {
                itemIds = dataObject["h_srp_items_loaded_078"]?.ToObject<List<string>>();
            }
        }

        return itemIds ?? new List<string>();
    }

    private static Item GetItemParams(string htmlContent, string itemId)
    {
        var item = new Item();
        item.Id = itemId;
        HtmlDocument htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(htmlContent);

        // Find the div with data-test="result-list"
        HtmlNode resultListDiv = htmlDoc.DocumentNode
            .SelectSingleNode("//div[@data-test='result-list']");

        if (resultListDiv == null)
        {
            return item;
        }

        // Find the a tag where href ends with the item ID
        HtmlNode itemLink = resultListDiv.SelectSingleNode($".//a[contains(@href, '{itemId}')]");

        if (itemLink == null)
        {
            return item;
        }

        HtmlNode titleDiv = itemLink.SelectSingleNode(".//div[contains(@class, 'HgListingRoomsLivingSpacePrice_roomsLivingSpacePrice_M6Ktp')]");

        string fullText = string.Join(" ", titleDiv.Descendants()
                                          .Where(node => node.NodeType == HtmlNodeType.Text)
                                          .Select(node => node.InnerText.Trim())
                                          .Where(text => !string.IsNullOrEmpty(text)));

        item.Title = fullText;

        HtmlNode priceSpan = titleDiv.SelectSingleNode(".//span[contains(@class, 'HgListingRoomsLivingSpacePrice_price_u9Vee')]");
        item.Price = priceSpan.InnerText.Trim();

        // Extract the address information from the item link or surrounding elements
        // Assuming the address is in a sibling or child element of the <a> tag
        HtmlNode addressNode = itemLink.SelectSingleNode(".//address");

        item.Address = addressNode?.InnerText?.Trim() ?? String.Empty;
        string relativeUrl = itemLink.GetAttributeValue("href", null) ?? String.Empty;
        item.Url = relativeUrl != null ? $"{baseUrl}{relativeUrl}" : String.Empty;

        HtmlNode? activeSlideLi = itemLink.Descendants("li")
            .FirstOrDefault(li => li.GetAttributeValue("data-glide-index", "") == "0");

        HtmlNode? imgTag = activeSlideLi == null ? itemLink?.Descendants("img").FirstOrDefault() : activeSlideLi?.Descendants("img").FirstOrDefault();

        item.ImgSrc = imgTag?.GetAttributeValue("src", null) ?? String.Empty;

        return item;
    }

    private static async Task ShowToastNotification(Item item)
    {
        string folderPath = Path.Combine(Environment.CurrentDirectory, "DownloadedImages");
        string fileName = item.ImgSrc.Split('/')[^1] + ".png";
        string localFilePath = Path.Combine(folderPath, fileName);

        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
            Console.WriteLine($"Created folder: {folderPath}");
        }

        using (HttpClient client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:131.0) Gecko/20100101 Firefox/131.0");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/png,image/svg+xml,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br, zstd");
            client.DefaultRequestHeaders.Add("Connection", "keep-alive");

            try
            {
                var imageBytes = await client.GetByteArrayAsync(item.ImgSrc);

                await File.WriteAllBytesAsync(localFilePath, imageBytes);                
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download or save the image: {ex.Message}");

                return;
            }
        }

        // .NET apps must use one of the Windows TFMs, otherwise the toast sending and management APIs like Show() will be missing. Set your TFM to net6.0-windows10.0.17763.0 or later (in .csproj like <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>).
        // Please, refer to -> https://learn.microsoft.com/en-us/windows/apps/design/shell/tiles-and-notifications/send-local-toast?tabs=desktop-msix
        new ToastContentBuilder()
            .AddInlineImage(new Uri(localFilePath))
            .AddText($"Time: {DateTime.Now.ToString("HH:mm:ss")}")
            .AddText($"Address: {item.Address}")
            .AddText($"Title: {item.Title}")
            .AddButton(new ToastButton()
                .SetContent("View Property")
                .AddArgument("action", "openUrl")
                .AddArgument("url", item.Url)
                .SetBackgroundActivation())
            .Show();

        //.AddAudio(new Uri("ms-appx:///Audio/NotificationSound.mp3"));

        ToastNotificationManagerCompat.OnActivated += toastArgs =>
        {
            if (isUrlOpen) return;

            // Parse the arguments to check if this is the "openUrl" action
            ToastArguments args = ToastArguments.Parse(toastArgs.Argument);

            if (args.Contains("action") && args["action"] == "openUrl")
            {
                // Open the URL in the default browser
                if (args.TryGetValue("url", out string link))
                {
                    isUrlOpen = true;

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = link,
                        UseShellExecute = true // Required to open the URL in the default browser
                    });
                }

                Task.Delay(1000).ContinueWith(_ => isUrlOpen = false);
            }
        };
    }

    private static async Task SaveCurrentItemsAsync(List<Item> currentItems)
    {
        List<Item> existingItems = new List<Item>();
        string filePath = Path.Combine(dataFilePath, dataFileName);

        if (File.Exists(filePath))
        {
            // Read existing items from the file
            string jsonFromFile = File.ReadAllText(filePath);
            existingItems = JsonConvert.DeserializeObject<List<Item>>(jsonFromFile) ?? new List<Item>();
        }

        foreach (var newItem in currentItems)
        {
            // Check if the item already exists in the existing items
            if (!existingItems.Any(existingItem => existingItem.Id == newItem.Id))
            {
                existingItems.Add(newItem);
            }
        }

        string updatedJson = JsonConvert.SerializeObject(existingItems, Formatting.Indented);

        try
        {
            await File.WriteAllTextAsync(filePath, updatedJson);
        }
        catch (Exception e)
        {
            Console.WriteLine("Failed to update items!");
        }
    }
}

public class Item
{
    public string Id { get; set; }
    public string Url { get; set; }
    public string Address { get; set; }
    public string ImgSrc { get; set; }
    public string Price { get; set; }
    public string Title { get; set; }
}