using Newtonsoft.Json;
using System.Net;
using System.Text;

namespace WikiTasks
{
    class PetScanEntry
    {
        [JsonProperty(PropertyName = "title")]
        public string ArticleTitle;
        [JsonProperty(PropertyName = "q")]
        public string WikidataItem;
    }

    class PetScanResult3
    {
        [JsonProperty(PropertyName = "*")]
        public PetScanEntry[] Entries;
    }

    class PetScanResult2
    {
        [JsonProperty(PropertyName = "a")]
        public PetScanResult3 Unk;
    }

    class PetScanResult1
    {
        [JsonProperty(PropertyName = "*")]
        public PetScanResult2[] Unk;
    }

    class PetScan
    {
        public static PetScanEntry[] Query(string request)
        {
            WebClient wc = new WebClient();
            wc.Headers["Content-Type"] = "application/x-www-form-urlencoded";
            string json = Encoding.UTF8.GetString(wc.UploadData(
                "https://petscan.wmflabs.org/", Encoding.UTF8.GetBytes(request)));
            var petScanResult = JsonConvert.DeserializeObject<PetScanResult1>(json);
            return petScanResult.Unk[0].Unk.Entries;
        }
    }
}
