using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Renci.SshNet;

namespace FileMover
{
    class Program
    {
        static void Main(string[] args)
        {
            // Define source and destination directories
            string ets1SourceDir = @"C:\Users\Ezer\Desktop\datalogs\Ondatalog\ETS Folder\ETS\ETS#1";
            string ets2SourceDir = @"C:\Users\Ezer\Desktop\datalogs\Ondatalog\ETS Folder\ETS\ETS#2";
            string backupDir = @"C:\Users\Ezer\Desktop\datalogs\Ondatalog\ETS Folder\ETS backup 1";
            string backupDir2 = @"C:\Users\Ezer\Desktop\datalogs\Ondatalog\ETS Folder\ETS backup 2";
            string zipDir = @"C:\Users\Ezer\Desktop\datalogs\Ondatalog\ETS Folder\ETS ZIP";
            string sftpHost = "sftp4.atecphil.com";
            string sftpUser = "onsemi_system";
            string sftpPassword = "ONSemi1*";
            string sftpRemoteDir = "/files/ASL/";

            // HashSet to store names of transferred ZIP files
            var transferredFiles = new HashSet<string>();

            try
            {
                ProcessSourceDirectory(ets1SourceDir, backupDir, backupDir2, zipDir, sftpHost, sftpUser, sftpPassword, sftpRemoteDir, transferredFiles, true);
                ProcessSourceDirectory(ets2SourceDir, backupDir, backupDir2, zipDir, sftpHost, sftpUser, sftpPassword, sftpRemoteDir, transferredFiles, false);

                Console.WriteLine("All files have been moved, zipped, transferred, and backed up successfully.");
            }
            catch (Exception e)
            {
                Console.WriteLine($"An error occurred: {e.Message}");
            }
        }

        static void ProcessSourceDirectory(string sourceDir, string backupDir, string backupDir2, string zipDir, string sftpHost, string sftpUser, string sftpPassword, string sftpRemoteDir, HashSet<string> transferredFiles, bool isETS1)
        {
            // Ensure the source directory exists
            if (Directory.Exists(sourceDir))
            {
                // Get all files recursively in the source directory
                var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    try
                    {
                        // Extract the file name
                        var fileName = Path.GetFileName(file);

                        // Extract the device and lot number
                        var parts = fileName.Split('_');
                        if (parts.Length < 7) continue;

                        var device = parts[0];
                        var lotNumber = parts[isETS1 ? 3 : 1]; // Adjust based on whether it's ETS#1 or ETS#2
                        var deviceLotNumber = $"{device}_{lotNumber}";

                        if (!string.IsNullOrEmpty(deviceLotNumber))
                        {
                            string newDir = Path.Combine(backupDir, deviceLotNumber);

                            if (!Directory.Exists(newDir))
                            {
                                Directory.CreateDirectory(newDir);
                            }

                            string newFilePath = Path.Combine(newDir, fileName);

                            File.Move(file, newFilePath);

                            Console.WriteLine($"Moved {fileName} to {newFilePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"An error occurred while processing the file {file}: {ex.Message}");
                    }
                }

                // Process backup directory for ZIP creation
                var directories = Directory.GetDirectories(backupDir);
                foreach (var dir in directories)
                {
                    var filesInDir = Directory.GetFiles(dir);
                    var groupedFiles = filesInDir
                        .Where(f => (f.EndsWith(".std_1") || f.EndsWith("SUMMARY_REPORT.txt")) &&
                                    (f.Contains("_P1_") || f.Contains("_P2_") || f.Contains("_P3_") ||
                                     f.Contains("_R1_") || f.Contains("_R2_") || f.Contains("_R3_") ||
                                     f.Contains("_QA_") || f.Contains("_Q1_") || f.Contains("_Q2_") || f.Contains("_Q3_")))
                        .GroupBy(f =>
                        {
                            var parts = Path.GetFileNameWithoutExtension(f).Split('_');
                            return string.Join("_", parts.Take(parts.Length - 3)); // Group by device, lot number, and first identifier parts
                        });

                    foreach (var group in groupedFiles)
                    {
                        string deviceLotNumber = group.Key;
                        var stdfFile = group.FirstOrDefault(f => f.EndsWith(".std_1"));
                        var summaryFile = group.FirstOrDefault(f => f.EndsWith("SUMMARY_REPORT.txt"));

                        if (stdfFile != null && summaryFile != null)
                        {
                            string zipFileName = Path.Combine(zipDir, deviceLotNumber + ".zip");

                            if (transferredFiles.Contains(zipFileName))
                            {
                                Console.WriteLine($"ZIP already transferred: {zipFileName}");
                                continue;
                            }

                            // Create the ZIP file
                            using (FileStream zipToOpen = new FileStream(zipFileName, FileMode.Create))
                            {
                                using (ZipArchive archive = new ZipArchive(zipToOpen, ZipArchiveMode.Update))
                                {
                                    archive.CreateEntryFromFile(stdfFile, Path.GetFileName(stdfFile));
                                    archive.CreateEntryFromFile(summaryFile, Path.GetFileName(summaryFile));
                                }
                            }

                            Console.WriteLine($"Created ZIP: {zipFileName}");

                            // Transfer the ZIP file to the SFTP server
                            try
                            {
                                using (var sftp = new SftpClient(sftpHost, sftpUser, sftpPassword))
                                {
                                    sftp.Connect();
                                    Console.WriteLine("Connected to SFTP server.");

                                    using (var fileStream = new FileStream(zipFileName, FileMode.Open))
                                    {
                                        sftp.UploadFile(fileStream, Path.Combine(sftpRemoteDir, Path.GetFileName(zipFileName)));
                                    }

                                    Console.WriteLine($"Transferred {zipFileName} to SFTP server.");
                                    sftp.Disconnect();
                                }

                                // Add the transferred ZIP file to the HashSet
                                transferredFiles.Add(zipFileName);

                                foreach (var file in group)
                                {
                                    string backupDir2Lot = Path.Combine(backupDir2, deviceLotNumber);

                                    if (!Directory.Exists(backupDir2Lot))
                                    {
                                        Directory.CreateDirectory(backupDir2Lot);
                                    }

                                    string backupFilePath = Path.Combine(backupDir2Lot, Path.GetFileName(file));

                                    File.Move(file, backupFilePath);

                                    Console.WriteLine($"Moved {file} to {backupFilePath}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"An error occurred while transferring the file {zipFileName} to SFTP server: {ex.Message}");
                            }
                        }
                    }
                }

                // Remove original files after processing
                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                        Console.WriteLine($"Deleted original file: {file}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"An error occurred while deleting the file {file}: {ex.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine($"The source directory {sourceDir} does not exist.");
            }
        }
    }
}
