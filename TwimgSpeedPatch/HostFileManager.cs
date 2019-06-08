﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TwimgSpeedPatch
{
    public static class HostFileManager
    {
        private const int HttpTestTimeout = 10000;
        private const int PingTimeout     = 1000;
        private const int PingTest        = 50;

        private static readonly string HostFileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers/etc/hosts");
        private static readonly string HostName = "pbs.twimg.com";
        private static readonly string PbsTestUriFormat = "https://{0}/media/CgAc2lSUMAA30oE.jpg:orig";

        private static readonly Regex RegPattern = new Regex($@"$[^\s\t#].+[\s\t]+{HostName}\t?^", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static event Action<int> PingProgressChanged;

        public static Task<string> PatchAsync()
        {
            return Task.Factory.StartNew(InnerPatch, true);
        }

        public static Task<string> UnpatchAsync()
        {
            return Task.Factory.StartNew(InnerPatch, false);
        }

        [DebuggerDisplay("Addr : {IPAddress} / {TotalMs} ms / failed : {Failed}")]
        private struct PingResult
        {
            public IPAddress IPAddress;
            public int       Failed;
            public long      TotalMs;
        }
        private static string GetBestIp()
        {
            JObject jo;
            using (var wc = new WebClient())
                jo = JObject.Parse(wc.DownloadString($"https://www.threatcrowd.org/searchApi/v2/domain/report/?domain={HostName}"));

            var minDate = DateTime.Now.Date.Subtract(TimeSpan.FromDays(365));

            var pings = jo["resolutions"]
                .Where(e => e["last_resolved"].Value<DateTime>() >= minDate)
                .Select(e => IPAddress.TryParse(e["ip_address"].Value<string>(), out var addr) ? addr : null)
                .Where(e => e != null)
                .Select(e => new PingResult { IPAddress = e })
                .ToArray();

            var progress = 0;
            Parallel.For(
                0,
                pings.Length,
                () => new Ping(),
                (index, state, ping) =>
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(string.Format(PbsTestUriFormat, pings[index].IPAddress));
                        req.Host = HostName;
                        req.Timeout = HttpTestTimeout;

                        using (var response = req.GetResponse())
                        {
                            if (!response.Headers[HttpResponseHeader.ContentType].StartsWith("image"))
                                throw new Exception();
                        }
                    }
                    catch
                    {
                        pings[index].Failed = PingTest;

                        PingProgressChanged?.Invoke(Interlocked.Increment(ref progress) * 100 / pings.Length);
                        return ping;
                    }

                    for (int i = 0; i < PingTest; ++i)
                    {
                        var reply = ping.Send(pings[index].IPAddress, PingTimeout);

                        if (reply.Status != IPStatus.Success)
                            pings[index].Failed++;
                        else
                            pings[index].TotalMs += reply.RoundtripTime;
                    }

                    PingProgressChanged?.Invoke(Interlocked.Increment(ref progress) * 100 / pings.Length);
                    return ping;
                },
                e => e.Dispose()
                );

            if (pings.All(e => e.Failed != 0))
                throw new Exception("최적의 서버를 찾지 못하였습니다. 잠시 후 다시 시도해주세요.");

            pings = pings.OrderBy(e => e.Failed).ThenBy(e => e.TotalMs).ToArray();

            return pings.OrderBy (e => e.Failed).ThenBy(e => e.TotalMs).First().IPAddress.ToString();
        }

        private static string InnerPatch(object patchObject)
        {
            var patch = (bool)patchObject;

            string newAddr = null;

            if (patch)
            {
                try
                {
                    newAddr = GetBestIp();
                }
                catch (Exception e)
                {
                    return e.ToString();
                }
            }

            try
            {
                using (var fs = new FileStream(HostFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var reader = new StreamReader(fs, Encoding.UTF8);
                    var writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true, NewLine = "\r\n" };

                    // read hosts
                    var lines = new List<string>();
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                            lines.Add(line);
                    }

                    // 기존 항목 제거
                    int i = 0;
                    while (i < lines.Count)
                    {
                        if (RegPattern.IsMatch(lines[i]))
                            lines.RemoveAt(i);
                        else
                            i++;
                    }

                    // 추가일 경우 한줄 더 쓰기
                    if (patch)
                    {
                        lines.Add(null);
                        lines.Add($"{newAddr}\t{HostName}");
                    }

                    // 다시 쓰기
                    fs.SetLength(0);
                    foreach (var line in lines)
                        writer.WriteLine(line);
                }
            }
            catch (Exception e)
            {
                return e.ToString();
            }


            Process.Start(new ProcessStartInfo { FileName = "ipconfig", Arguments = "/flushdns", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }).Dispose();
            return null;
        }

        public static void OpenHostFile()
        {
            Process.Start("notepad.exe", HostFileName);
        }
    }
}
