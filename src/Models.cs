using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Script.Serialization;

namespace NexusM
{
    // The API adds fields regularly; preserve them instead of maintaining a lossy DTO.
    public static class JsonTools
    {
        public static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 24 * 1024 * 1024, RecursionLimit = 100 };
        }

        public static Dictionary<string, object> Dict(object value)
        {
            return value as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        public static object[] Array(object value)
        {
            if (value is object[]) return (object[])value;
            var sequence = value as IEnumerable;
            if (sequence == null || value is string || value is IDictionary) return new object[0];
            var list = new List<object>();
            foreach (object item in sequence) list.Add(item);
            return list.ToArray();
        }

        public static object Get(Dictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) ? result : null;
        }

        public static string Text(Dictionary<string, object> value, string key)
        {
            return Convert.ToString(Get(value, key), CultureInfo.InvariantCulture) ?? "";
        }

        public static long Long(Dictionary<string, object> value, string key)
        {
            long result;
            return long.TryParse(Text(value, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        public static long UnixNow()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public static string Domain(string domain)
        {
            if (String.IsNullOrWhiteSpace(domain) || !Regex.IsMatch(domain, "^[A-Za-z0-9_-]{1,100}$"))
                throw new ArgumentException("Enter a valid Nexus game domain.");
            return domain.ToLowerInvariant();
        }
    }

    public sealed class NexusApiException : Exception
    {
        public int StatusCode { get; private set; }
        public NexusApiException(string message, int statusCode) : base(message) { StatusCode = statusCode; }
    }

    public sealed class DownloadRequest
    {
        public string Domain;
        public long ModId;
        public long FileId;
        public string Folder;
        public string Key;
        public long Expires;
        public long UserId;

        public string Id { get { return Domain + ":" + ModId + ":" + FileId; } }

        public static DownloadRequest ParseNxm(string value, string folder)
        {
            Uri uri;
            if (String.IsNullOrWhiteSpace(value) || value.Length > 8192 ||
                !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "nxm" ||
                !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Fragment) || uri.Port != -1)
                throw new ArgumentException("This is not a valid Nexus download link.");
            Match path = Regex.Match(uri.AbsolutePath, "^/mods/([1-9][0-9]*)/files/([1-9][0-9]*)/?$", RegexOptions.IgnoreCase);
            long modId, fileId;
            if (!path.Success || !long.TryParse(path.Groups[1].Value, out modId) || !long.TryParse(path.Groups[2].Value, out fileId))
                throw new ArgumentException("Only Nexus mod-file links are supported.");
            var query = HttpUtility.ParseQueryString(uri.Query);
            long expires = 0, user = 0;
            if (!String.IsNullOrEmpty(query["expires"]) && !long.TryParse(query["expires"], out expires))
                throw new ArgumentException("The Nexus download link has an invalid expiry.");
            if (!String.IsNullOrEmpty(query["user_id"]) && !long.TryParse(query["user_id"], out user))
                throw new ArgumentException("The Nexus download link has an invalid account ID.");
            return new DownloadRequest { Domain = JsonTools.Domain(uri.Host), ModId = modId, FileId = fileId,
                Key = query["key"], Expires = expires, UserId = user, Folder = folder };
        }
    }
}
