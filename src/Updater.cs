﻿using BsDiff;
using SharpCompress.Readers;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

namespace CM_Launcher
{
    public partial class Updater : Form, IProgress<float>
    {
        private readonly SynchronizationContext synchronizationContext;

        public Updater()
        {
            InitializeComponent();
            synchronizationContext = SynchronizationContext.Current;

            new Thread(() =>
            {
                new Main(this);
            }).Start();
        }

        public void Show(int current, int desired)
        {
            DoUpdate(current, desired);
        }

        private void UpdateLabel(string text)
        {
            synchronizationContext.Post(new SendOrPostCallback(o =>
            {
                label1.Text = $"{o}";
            }), text);
        }

        private async Task<int> UpdateUsingZip(int version)
        {
            UpdateLabel("Downloading update...");
            string downloadUrl = Main.USE_CDN ? $"{Main.CDN_URL}/{version}/Win64.zip" : $"https://jenkins.kirkstall.top-cat.me/job/ChroMapper/{version}/artifact/ChroMapper-Win64.zip";

            using (var tmp = new TempFile())
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(5);

                    using (var file = new FileStream(tmp.Path, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await client.DownloadAsync(downloadUrl, file, this);
                    }
                }

                ExtractZip(tmp.Path);

                SetVersion(version);

                return version;
            }
        }

        private async Task<int> UpdateUsingPatch(int source, int dest)
        {
            UpdateLabel($"Downloading patch for {dest}");
            string downloadUrl = $"{Main.CDN_URL}/{dest}/{source}.patch";

            using (var tmp = new TempFile())
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(5);

                    using (var file = new FileStream(tmp.Path, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await client.DownloadAsync(downloadUrl, file, this);
                    }
                }

                ApplyPatch(tmp.Path);
                SetVersion(dest);

                return dest;
            }
        }

        private void ApplyPatch(string filename)
        {
            Report(0);
            using (Stream stream = File.OpenRead(filename))
            {
                var reader = ReaderFactory.Open(stream);
                while (reader.MoveToNextEntry())
                {
                    if (!reader.Entry.IsDirectory)
                    {
                        string keyFilename = reader.Entry.Key;
                        string patchFilename = keyFilename;
                        string compressionType = keyFilename.Substring(0, keyFilename.IndexOf("/"));

                        if (compressionType == "xdelta" || compressionType == "bsdiff")
                        {
                            keyFilename = keyFilename.Substring(compressionType.Length + 1);
                            patchFilename = patchFilename.Replace(compressionType, "chromapper");
                        }
                        else
                        {
                            compressionType = "";
                            patchFilename = $"chromapper/{patchFilename}";
                        }

                        UpdateLabel($"Patching {keyFilename}");
                        Report(((float) stream.Position) / stream.Length);

                        using (MemoryStream memStream = new MemoryStream(100))
                        {
                            reader.WriteEntryTo(memStream);

                            byte[] newFile = memStream.ToArray();

                            if (compressionType == "xdelta")
                            {
                                byte[] oldFile = File.ReadAllBytes(patchFilename);
                                newFile = xdelta3.ApplyPatch(memStream.ToArray(), oldFile);
                            }
                            else if (compressionType == "bsdiff")
                            {
                                using (FileStream input = new FileStream(patchFilename, FileMode.Open, FileAccess.Read, FileShare.Read))
                                using (MemoryStream memStream2 = new MemoryStream(100)) {
                                    BinaryPatchUtility.Apply(input, () => new MemoryStream(newFile), memStream2);
                                    newFile = memStream2.ToArray();
                                }
                            }
                            File.WriteAllBytes(patchFilename, newFile);
                        }
                    }
                }
            }
        }

        private void ExtractZip(string filename)
        {
            UpdateLabel("Extracting zip");
            Report(0);

            using (var archive = ZipFile.Open(filename, ZipArchiveMode.Read))
            {
                string destinationDirectoryFullPath = Main.LauncherFolder;
                float total = archive.Entries.Count();
                int current = 0;

                foreach (ZipArchiveEntry file in archive.Entries)
                {
                    string completeFileName = Path.GetFullPath(Path.Combine(destinationDirectoryFullPath, file.FullName));

                    UpdateLabel($"Extracting {file.FullName.Replace("chromapper/", "")}");
                    Report(current++ / total);

                    if (!completeFileName.StartsWith(destinationDirectoryFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("Trying to extract file outside of destination directory");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(completeFileName));
                    if (file.Name == "") // Assuming Empty for Directory
                    {
                        continue;
                    }

                    file.ExtractToFile(completeFileName, true);
                }
            }
        }

        private void SetVersion(int version)
        {
            Version.GetVersion().Update(version, Main.CDN_URL);
        }

        private async Task<List<int>> FindPath(int current, int desired)
        {
            var regex = new Regex(@"[0-9]+/([0-9]+).patch");
            var patches = new List<int>();

            using (HttpClient client = new HttpClient())
            {
                using (HttpResponseMessage response = await client.GetAsync($"{Main.CDN_URL}?prefix={desired}/"))
                {
                    using (HttpContent content = response.Content)
                    {
                        var stream = await content.ReadAsStreamAsync();
                        XmlReader xReader = XmlReader.Create(stream);

                        while (xReader.ReadToFollowing("Contents"))
                        {
                            xReader.ReadToFollowing("Key");
                            var str = xReader.ReadElementContentAsString();
                            var matches = regex.Matches(str);

                            if (matches.Count > 0)
                            {
                                int possible = int.Parse(matches[0].Groups[1].Value);
                                patches.Add(possible);
                            }
                        }
                    }
                }
            }
            
            if (patches.Contains(current))
            {
                // We can patch from here!
                var ret = new List<int>
                {
                    desired
                };
                return ret;
            }

            try
            {
                var oldest = patches.Where(a => a > current).Min();
                var ret2 = await FindPath(current, oldest);

                if (ret2 != null)
                    ret2.Add(desired);

                return ret2;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private async void DoUpdate(int current, int desired)
        {
            int stable = await Main.GetLatestBuildNumber(ReleaseChannel.Stable);
            var version = Version.GetVersion();

            if (version.VersionServer != Main.CDN_URL || current > desired)
            {
                current = await UpdateUsingZip(stable);
            }

            if (current < desired)
            {
                var patches = await FindPath(current, desired);

                if (patches == null)
                {
                    // We can't patch to the desired version
                    // If we're not on stable go there
                    if (current != stable)
                    {
                        await UpdateUsingZip(stable);
                    }

                    // Abandon ship!
                    // Hopefully someone creates an update path for us next time around
                    Main.Exit();
                    return;
                }

                foreach (int patch in patches)
                {
                    current = await UpdateUsingPatch(current, patch);
                }
            }

            Main.Exit();
        }

        public void Report(float value)
        {
            synchronizationContext.Post(new SendOrPostCallback(o =>
            {
                progressBar1.Value = (int) o;
            }), (int)(value * 1000));
        }
    }
}
