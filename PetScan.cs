using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace WikiTasks
{
    class PetScanEntry
    {
        [JsonProperty(PropertyName = "id")]
        public int Id;
        [JsonProperty(PropertyName = "title")]
        public string Title;
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

    class PetScan : Api
    {
        static readonly Dictionary<string, string> defaultParameters;

        static PetScan()
        {
            defaultParameters = new Dictionary<string, string>()
            {
                { "language", "en" },
                { "project", "wikipedia" },
                { "depth", "0" },
                { "categories", "" },
                { "combination", "subset" },
                { "negcats", "" },
                { "ns[0]", "1" },
                { "larger", "" },
                { "smaller", "" },
                { "minlinks", "" },
                { "maxlinks", "" },
                { "before", "" },
                { "after", "" },
                { "max_age", "" },
                { "show_redirects", "both" },
                { "edits[bots]", "both" },
                { "edits[anons]", "both" },
                { "edits[flagged]", "both" },
                { "templates_yes", "" },
                { "templates_any", "" },
                { "templates_no", "" },
                { "outlinks_yes", "" },
                { "outlinks_any", "" },
                { "outlinks_no", "" },
                { "links_to_all", "" },
                { "links_to_any", "" },
                { "links_to_no", "" },
                { "sparql", "" },
                { "manual_list", "" },
                { "manual_list_wiki", "" },
                { "pagepile", "" },
                { "wikidata_source_sites", "" },
                { "subpage_filter", "either" },
                { "common_wiki", "auto" },
                { "source_combination", "" },
                { "wikidata_item", "no" },
                { "wikidata_label_language", "" },
                { "wikidata_prop_item_use", "" },
                { "wpiu", "any" },
                { "sitelinks_yes", "" },
                { "sitelinks_any", "" },
                { "sitelinks_no", "" },
                { "min_sitelink_count", "" },
                { "max_sitelink_count", "" },
                { "labels_yes", "" },
                { "cb_labels_yes_l", "1" },
                { "langs_labels_yes", "" },
                { "labels_any", "" },
                { "cb_labels_any_l", "1" },
                { "langs_labels_any", "" },
                { "labels_no", "" },
                { "cb_labels_no_l", "1" },
                { "langs_labels_no", "" },
                { "format", "json" },
                { "output_compatability", "catscan" },
                { "sortby", "none" },
                { "sortorder", "ascending" },
                { "regexp_filter", "" },
                { "min_redlink_count", "1" },
                { "doit", "Do it!" },
                { "interface_language", "en" },
                { "active_tab", "tab_output" }
            };
        }

        public static PetScanEntry[] Query(params string[] additionalParameters)
        {
            if (additionalParameters.Length % 2 != 0)
                throw new Exception();

            var allParameters = new Dictionary<string, string>(defaultParameters);
            for (int i = 0; i < additionalParameters.Length / 2; i++)
            {
                var k = additionalParameters[i * 2];
                var v = additionalParameters[i * 2 + 1];
                if (v != null)
                    allParameters[k] = v;
                else
                    allParameters.Remove(k);
            }

            var postBody = string.Join("&", allParameters.Select(
                p => UrlEncode(p.Key) + "=" + UrlEncode(p.Value)));

            WebClient wc = new WebClient();
            wc.Headers["Content-Type"] = "application/x-www-form-urlencoded";
            string json = Encoding.UTF8.GetString(wc.UploadData(
                "https://petscan.wmflabs.org/", Encoding.UTF8.GetBytes(postBody)));
            var petScanResult = JsonConvert.DeserializeObject<PetScanResult1>(json);
            return petScanResult.Unk[0].Unk.Entries;
        }
    }
}
