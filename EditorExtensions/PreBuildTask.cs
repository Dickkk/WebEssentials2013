﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Framework;

namespace MadsKristensen.EditorExtensions
{
    /// <summary>
    /// This is not compiled (ItemType=None) but is invoked by the Inline Task (http://msdn.microsoft.com/en-us/library/dd722601) in the csproj file.
    /// </summary>
    public class PreBuildTask : Microsoft.Build.Utilities.Task
    {
        public override bool Execute()
        {
            var webclient = new WebClient();

            Directory.CreateDirectory(@"resources\nodejs");
            if (!File.Exists(@"resources\nodejs\node.exe"))
            {
                Log.LogMessage(MessageImportance.High, "Downloading nodejs ...");
                webclient.DownloadFile("http://nodejs.org/dist/latest/node.exe", @"resources\nodejs\node.exe");
            }

            if (!File.Exists(@"resources\nodejs\node_modules\npm\bin\npm.cmd"))
            {
                Log.LogMessage(MessageImportance.High, "Downloading npm ...");
                var npmZip = webclient.OpenRead("http://nodejs.org/dist/npm/npm-1.3.13.zip");
                try
                {
                    ExtractZipWithOverwrite(npmZip, @"resources\nodejs");
                }
                catch
                {
                    // Make sure the next build doesn't see a half-installed npm
                    Directory.Delete(@"resources\nodejs\node_modules\npm", true);
                    throw;
                }
            }

            InstallModule("lessc", "less");
            InstallModule("coffee", "coffee-script");
            InstallModule("iced", "iced-coffee-script");

            return true;
        }

        void InstallModule(string cmdName, string moduleName)
        {
            if (!File.Exists(@"resources\nodejs\node_modules\.bin\" + cmdName + ".cmd"))
            {
                Log.LogMessage(MessageImportance.High, "npm install " + moduleName + " ...");
                var output = new StringWriter();
                int result = Exec(@"cmd", @"/c .\npm.cmd install " + moduleName, @"resources\nodejs", output, output);
                if (result != 0)
                {
                    Log.LogError("npm error " + result + ": " + output.ToString().Trim());
                }
                FlattenNodeModules(@"resources\nodejs\node_modules\" + moduleName + "\node_modules");
            }

        }

        /// <summary>
        /// Due to the way node_modues work, the directory depth can get very deep and go beyond MAX_PATH (260 chars). 
        /// Therefore grab all node_modues directories and move them up to baseNodeModuleDir. Node's require() will then 
        /// traverse up and find them at the higher level. Should be fine as long as there are no versioning conflicts.
        /// </summary>
        static void FlattenNodeModules(string baseNodeModuleDir)
        {
            var baseDir = new DirectoryInfo(baseNodeModuleDir);

            var nodeModulesDirs = from dir in baseDir.EnumerateDirectories("*", SearchOption.AllDirectories)
                                  where dir.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                                  orderby dir.FullName.Count(c => c == Path.DirectorySeparatorChar) descending // Get deepest first
                                  select dir;

            foreach (var nodeModules in nodeModulesDirs)
            {
                foreach (var module in nodeModules.EnumerateDirectories())
                {
                    string targetDir = Path.Combine(baseDir.FullName, module.Name);
                    if (!Directory.Exists(targetDir))
                        module.MoveTo(targetDir);
                }

                if (!nodeModules.EnumerateFileSystemInfos().Any())
                    nodeModules.Delete();
            }
        }

        /// <summary>Invokes a command-line process.</summary>
        static int Exec(string filename, string args, string workingDirectory = null, TextWriter stdout = null, TextWriter stderr = null)
        {
            stdout = stdout ?? TextWriter.Null;
            stderr = stderr ?? TextWriter.Null;

            var p = new Process
            {
                StartInfo = new ProcessStartInfo(filename, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = workingDirectory == null ? null : Path.GetFullPath(workingDirectory),
                },
                EnableRaisingEvents = true,
            };

            p.OutputDataReceived += (sender, e) =>
            {
                stdout.WriteLine(e.Data);
            };
            p.ErrorDataReceived += (sender, e) =>
            {
                stderr.WriteLine(e.Data);
            };

            p.Start();
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            p.WaitForExit();
            return p.ExitCode;
        }

        void ExtractZipWithOverwrite(Stream sourceZip, string destinationDirectoryName)
        {
            using (var source = new ZipArchive(sourceZip, ZipArchiveMode.Read))
            {
                foreach (var entry in source.Entries)
                {
                    const string prefix = "node_modules/npm/node_modules/";

                    // Collapse nested node_modules folders to avoid MAX_PATH issues from Path.GetFullPath
                    var targetSubPath = entry.FullName;
                    if (targetSubPath.StartsWith(prefix) && targetSubPath.Length > prefix.Length)
                    {
                        // If there is another node_modules folder after the prefix, collapse them
                        var lastModule = entry.FullName.LastIndexOf("node_modules/");
                        if (lastModule > prefix.Length)
                            targetSubPath = targetSubPath.Remove(prefix.Length, lastModule + "node_modules/".Length - prefix.Length);
                        Log.LogMessage(MessageImportance.High, entry.FullName + "\t=> " + targetSubPath);
                    }

                    var targetPath = Path.GetFullPath(Path.Combine(destinationDirectoryName, targetSubPath));

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                    if (!targetPath.EndsWith(@"\"))
                        entry.ExtractToFile(targetPath, overwrite: true);
                }
            }
        }
    }
}
