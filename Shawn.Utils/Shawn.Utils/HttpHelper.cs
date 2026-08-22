using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Shawn.Utils
{
    public static class HttpHelper
    {
        #region POST

        public static string Post(string url, Dictionary<string, string> dic, Encoding? encoding = null)
        {
            return PostAsync(url, dic, encoding).Result;
        }

        public static string Post(string url, string content, Encoding? encoding = null)
        {
            return PostAsync(url, content, encoding).Result;
        }

        public static async Task<string> PostAsync(string url, Dictionary<string, string> dic, Encoding? encoding = null)
        {
            var builder = new StringBuilder();
            var i = 0;
            foreach (var item in dic)
            {
                if (i > 0)
                    builder.Append('&');
                builder.AppendFormat("{0}={1}", item.Key, item.Value);
                i++;
            }
            return await PostAsync(url, builder.ToString(), encoding);
        }

        public static async Task<string> PostAsync(string url, string content, Encoding? encoding = null)
        {
            using var client = new HttpClient();
            using var response = await client.PostAsync(url, new StringContent(content, Encoding.UTF8));
            var bytes = await response.Content.ReadAsByteArrayAsync();
            encoding ??= Encoding.UTF8;
            return encoding.GetString(bytes);
        }

        #endregion POST

        #region GET

        public static string Get(string url, Encoding? encoding = null)
        {
            return GetAsync(url, encoding).Result;
        }

        public static string Get(string url, Dictionary<string, string> dic, Encoding? encoding = null)
        {
            return GetAsync(url, dic, encoding).Result;
        }

        public static async Task<string> GetAsync(string url, Encoding? encoding = null)
        {
            using var client = new HttpClient();
            var bytes = await client.GetByteArrayAsync(url);
            encoding ??= Encoding.UTF8;
            return encoding.GetString(bytes);
        }

        public static async Task<string> GetAsync(string url, Dictionary<string, string> dic, Encoding? encoding = null)
        {
            var builder = new StringBuilder(url);
            if (dic.Count > 0)
            {
                builder.Append('?');
                var i = 0;
                foreach (var item in dic)
                {
                    if (i > 0)
                        builder.Append('&');
                    builder.AppendFormat("{0}={1}", item.Key, item.Value);
                    i++;
                }
            }
            return await GetAsync(builder.ToString(), encoding);
        }

        #endregion GET
    }
}
