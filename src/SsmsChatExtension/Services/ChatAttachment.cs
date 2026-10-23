using System;
using System.IO;
using System.Linq;

namespace SsmsChatExtension.Services
{
    /// <summary>
    /// A file or screenshot attached to a chat message. Attachments are copied into <see cref="Root"/>, so the
    /// chat service sees a stable snapshot even if the original changes or was only on the clipboard.
    /// </summary>
    public sealed class ChatAttachment
    {
        public const long MaxBytes = 20 * 1024 * 1024;
        public const int MaxPerMessage = 10;

        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
        private static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

        private ChatAttachment(string name, string path, long size)
        {
            Name = name;
            Path = path;
            Size = size;
        }

        /// <summary>Folder that holds every attachment (one sub-folder each); the Claude CLI gets it via --add-dir.</summary>
        public static string Root
        {
            get
            {
                return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                              "SsmsChatExtension", "attachments");
            }
        }

        /// <summary>File name shown in the chat.</summary>
        public string Name { get; }

        /// <summary>Full path of the stored copy.</summary>
        public string Path { get; }

        public long Size { get; }

        public bool IsImage
        {
            get { return ImageExtensions.Contains(System.IO.Path.GetExtension(Name), StringComparer.OrdinalIgnoreCase); }
        }

        public string DisplaySize
        {
            get
            {
                if (Size < 1024) return Size + " B";
                if (Size < 1024 * 1024) return (Size / 1024.0).ToString("0.#") + " KB";
                return (Size / (1024.0 * 1024)).ToString("0.#") + " MB";
            }
        }

        /// <summary>Copies <paramref name="sourcePath"/> into the attachment store.</summary>
        public static ChatAttachment FromFile(string sourcePath)
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists) throw new FileNotFoundException("File not found: " + sourcePath);
            CheckSize(info.Name, info.Length);

            var target = System.IO.Path.Combine(NewFolder(), info.Name);
            File.Copy(info.FullName, target);
            return new ChatAttachment(info.Name, target, info.Length);
        }

        /// <summary>Stores <paramref name="content"/> (for example a PNG screenshot) as <paramref name="name"/>.</summary>
        public static ChatAttachment FromBytes(string name, byte[] content)
        {
            CheckSize(name, content.LongLength);

            var target = System.IO.Path.Combine(NewFolder(), name);
            File.WriteAllBytes(target, content);
            return new ChatAttachment(name, target, content.LongLength);
        }

        /// <summary>Removes attachments older than 30 days. Errors are ignored; this is housekeeping only.</summary>
        public static void DeleteOld()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                foreach (var folder in Directory.GetDirectories(Root))
                {
                    try
                    {
                        if (DateTime.UtcNow - Directory.GetCreationTimeUtc(folder) > KeepFor) Directory.Delete(folder, true);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void CheckSize(string name, long size)
        {
            if (size > MaxBytes)
            {
                throw new InvalidOperationException(name + " is larger than " + (MaxBytes / (1024 * 1024)) + " MB.");
            }
        }

        // One folder per attachment, so two files with the same name never collide.
        private static string NewFolder()
        {
            var folder = System.IO.Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}
