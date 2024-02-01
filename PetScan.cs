using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace WikiTasks
{
    class PetScanEntry
    {
        [JsonProperty(PropertyName = "id")]
        public int Id;
        [JsonProperty(PropertyName = "len")]
        public int Size;
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
        [JsonProperty(PropertyName = "error")]
        public string Error;
    }

    class PetScan : Api
    {
        static readonly Dictionary<string, string> defaultParameters;

        static PetScan()
        {
            defaultParameters = new Dictionary<string, string>()
            {
                { "active_tab", "tab_output" },
                { "after", "" },
                { "before", "" },
                { "categories", "" },
                { "cb_labels_any_l", "1" },
                { "cb_labels_no_l", "1" },
                { "cb_labels_yes_l", "1" },
                { "combination", "subset" },
                { "common_wiki", "auto" },
                { "common_wiki_other", "" },
                { "depth", "0" },
                { "doit", "Do it!" },
                { "edits[anons]", "both" },
                { "edits[bots]", "both" },
                { "edits[flagged]", "both" },
                { "format", "json" },
                { "interface_language", "en" },
                { "labels_any", "" },
                { "labels_no", "" },
                { "labels_yes", "" },
                { "langs_labels_any", "" },
                { "langs_labels_no", "" },
                { "langs_labels_yes", "" },
                { "language", "en" },
                { "larger", "" },
                { "links_to_all", "" },
                { "links_to_any", "" },
                { "links_to_no", "" },
                { "manual_list", "" },
                { "manual_list_wiki", "" },
                { "max_age", "" },
                { "max_sitelink_count", "" },
                { "maxlinks", "" },
                { "min_redlink_count", "1" },
                { "min_sitelink_count", "" },
                { "minlinks", "" },
                { "namespace_conversion", "keep" },
                { "negcats", "" },
                { "ns[0]", "1" },
                { "ores_prediction", "any" },
                { "ores_prob_from", "" },
                { "ores_prob_to", "" },
                { "ores_type", "any" },
                { "outlinks_any", "" },
                { "outlinks_no", "" },
                { "outlinks_yes", "" },
                { "output_compatability", "catscan" },
                { "output_limit", "" },
                { "page_image", "any" },
                { "pagepile", "" },
                { "project", "wikipedia" },
                { "referrer_name", "" },
                { "referrer_url", "" },
                { "regexp_filter", "" },
                { "search_filter", "" },
                { "search_max_results", "500" },
                { "search_query", "" },
                { "search_wiki", "" },
                { "show_disambiguation_pages", "both" },
                { "show_redirects", "both" },
                { "show_soft_redirects", "both" },
                { "since_rev0", "" },
                { "sitelinks_any", "" },
                { "sitelinks_no", "" },
                { "sitelinks_yes", "" },
                { "smaller", "" },
                { "sortby", "none" },
                { "sortorder", "ascending" },
                { "source_combination", "" },
                { "sparql", "" },
                { "subpage_filter", "either" },
                { "templates_any", "" },
                { "templates_no", "" },
                { "templates_yes", "" },
                { "wikidata_item", "no" },
                { "wikidata_label_language", "" },
                { "wikidata_prop_item_use", "" },
                { "wikidata_source_sites", "" },
                { "wpiu", "any" }
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
            string json;
            int a = 0;
            for (;; a++)
            {
                if (a == 20)
                    throw new Exception("Exceeded attempt count for PetScan request");
                try
                {
                    json = Encoding.UTF8.GetString(wc.UploadData(
                        "https://petscan.wmflabs.org/", Encoding.UTF8.GetBytes(postBody)));
                }
                catch (WebException)
                {
                    Thread.Sleep(30000);
                    continue;
                }
                var petScanResult = JsonConvert.DeserializeObject<PetScanResult1>(json);
                if (petScanResult.Error == "No result for source categories")
                {
                    Thread.Sleep(60000);
                    continue;
                }
                return petScanResult.Unk[0].Unk.Entries;
            }
        }
    }
}
