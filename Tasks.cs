﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mygod.Xml.Linq;

namespace Mygod.Skylark
{
    public static class TaskType
    {
        public const string NoTask = "ready",
                            OfflineDownloadTask = "offline-download",
                            BitTorrentTask = "bit-torrent",
                            CompressTask = "compress",
                            DecompressTask = "decompress",
                            FtpUploadTask = "ftp-upload",
                            ConvertTask = "convert",
                            CrossAppCopyTask = "cross-app-copy";
    }
    public enum TaskStatus
    {
        Terminated, Working, Error, Starting, Done
    }
    public abstract partial class CloudTask
    {
        protected CloudTask(string filePath)
        {
            if (!string.IsNullOrEmpty(FilePath = filePath) && File.Exists(filePath))
                TaskXml = XHelper.Load(filePath).Root;
        }
        protected CloudTask(string filePath, string root)
        {
            FilePath = filePath;
            TaskXml = new XElement(root);
        }

        protected static readonly Regex
            AccountRemover = new Regex(@"^ftp:\/\/[^\/]*?:[^\/]*?@", RegexOptions.Compiled);

        public static void KillProcess(int pid)
        {
            try
            {
                Process.GetProcessById(pid).Kill();
            }
            catch { }
        }
        public static bool IsBackgroundRunnerKilled(int pid)
        {
            try
            {
                return Process.GetProcessById(pid).ProcessName != "BackgroundRunner";
            }
            catch
            {
                return true;
            }
        }

        protected readonly XElement TaskXml;
        protected readonly string FilePath;

        public int PID
        {
            get { return TaskXml == null ? 0 : TaskXml.GetAttributeValueWithDefault<int>("pid"); }
            set { TaskXml.SetAttributeValue("pid", value); }
        }
        public string ErrorMessage
        {
            get { return TaskXml == null ? "任务数据缺失！" : TaskXml.GetAttributeValue("message"); }
            set { TaskXml.SetAttributeValue("message", value); }
        }

        public abstract string Type { get; }

        public abstract DateTime? StartTime { get; set; }   // leave it for derived classes
        public virtual DateTime? EndTime
        {
            get
            {
                if (TaskXml == null) return null;
                var endTime = TaskXml.GetAttributeValueWithDefault<long>("endTime", -1);
                return endTime < 0 ? null
                    : (DateTime?)new DateTime(TaskXml.GetAttributeValue<long>("endTime"), DateTimeKind.Utc);
            }
            set
            {
                if (value.HasValue) TaskXml.SetAttributeValue("endTime", value.Value.Ticks);
                else TaskXml.SetAttributeValue("endTime", null);
            }
        }

        public virtual long? FileLength
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue<long?>("size"); }
            set
            {
                if (value.HasValue) TaskXml.SetAttributeValue("size", value.Value);
                else TaskXml.SetAttributeValue("size", null);
            }
        }
        public virtual long ProcessedFileLength
        {
            get { return TaskXml == null ? 0 : TaskXml.GetAttributeValueWithDefault<long>("sizeProcessed"); }
            set { TaskXml.SetAttributeValue("sizeProcessed", value); }
        }

        public double? SpeedFileLength
        {
            get { return SpentTime.HasValue ? (double?)ProcessedFileLength / SpentTime.Value.TotalSeconds : null; }
        }

        public virtual double? Percentage { get { return 100.0 * ProcessedFileLength / FileLength; } }

        public TaskStatus Status
        {
            get
            {
                return PID > 0
                            ? EndTime.HasValue
                                ? TaskStatus.Done
                                : string.IsNullOrEmpty(ErrorMessage)
                                    ? IsBackgroundRunnerKilled(PID) ? TaskStatus.Terminated : TaskStatus.Working
                                    : TaskStatus.Error
                            : TaskStatus.Starting;
            }
        }

        public TimeSpan? SpentTime
        {
            get
            {
                return Status == TaskStatus.Working ? DateTime.UtcNow - StartTime
                                                    : EndTime.HasValue ? EndTime.Value - StartTime : null;
            }
        }
        public TimeSpan? PredictedRemainingTime
        {
            get
            {
                return EndTime.HasValue
                    ? new TimeSpan()
                    : Percentage > 0 && SpentTime.HasValue
                        ? (TimeSpan?)new TimeSpan((long)((100 - Percentage) / Percentage * SpentTime.Value.Ticks))
                        : null;
            }
        }
        public DateTime? PredictedEndTime
        {
            get { return EndTime.HasValue ? EndTime.Value : StartTime + PredictedRemainingTime; }
        }

        public string GetStatus(string action, Action never)
        {
            switch (Status)
            {
                case TaskStatus.Terminated:
                    never();
                    return "已被终止（请删除后重新开始任务）";
                case TaskStatus.Working:
                    return "正在 " + action + " 中";
                case TaskStatus.Error:
                    never();
                    return "发生错误，具体信息：<br /><pre>" + ErrorMessage + "</pre>";
                case TaskStatus.Starting:
                    return "正在开始";
                case TaskStatus.Done:
                    return action + " 完毕";
                default:
                    return "未知";
            }
        }

        public void Save()
        {
            lock (TaskXml)
            {
                TaskXml.Save(FilePath);
            }
        }
    }
    public interface IRemoteTask
    {
        string Url { get; }
    }
    public interface ISingleSource
    {
        string Source { get; }
    }
    public interface IMultipleSources
    {
        string BaseFolder { get; }
        string CurrentSource { get; }
        long? SourceCount { get; }
        long ProcessedSourceCount { get; }
        IEnumerable<string> Sources { get; }
    }

    public abstract partial class GenerateFileTask : CloudTask
    {
        public static GenerateFileTask Create(string relativePath)
        {
            switch (FileHelper.GetState(FileHelper.GetDataFilePath(relativePath)).ToLowerInvariant())
            {
                case TaskType.OfflineDownloadTask:
                    return new OfflineDownloadTask(relativePath);
                case TaskType.CompressTask:
                    return new CompressTask(relativePath);
                case TaskType.ConvertTask:
                    return new ConvertTask(relativePath);
                default:
                    return null;
            }
        }

        protected GenerateFileTask(string relativePath) : base(FileHelper.GetDataFilePath(relativePath))
        {
            RelativePath = relativePath;
        }
        protected GenerateFileTask(string relativePath, string state)
            : base(FileHelper.GetDataFilePath(relativePath), "file")
        {
            RelativePath = relativePath;
            State = state;
            Mime = Helper.GetMimeType(relativePath);
            StartTime = DateTime.UtcNow;
            File.WriteAllText(FileHelper.GetFilePath(relativePath), string.Empty);  // temp
        }

        public override sealed string Type { get { return State; } }

        public override sealed DateTime? StartTime
        {
            get
            {
                return TaskXml == null
                    ? null : (DateTime?)new DateTime(TaskXml.GetAttributeValue<long>("startTime"), DateTimeKind.Utc);
            }
            set
            {
                if (value.HasValue) TaskXml.SetAttributeValue("startTime", value.Value.Ticks);
                else TaskXml.SetAttributeValue("startTime", null);
            }
        }

        public string State
        {
            get { return TaskXml.GetAttributeValue("state"); }
            set { TaskXml.SetAttributeValue("state", value); }
        }
        public string Mime
        {
            get { return TaskXml.GetAttributeValue("mime"); }
            set {  TaskXml.SetAttributeValue("mime", value); }
        }
        public string RelativePath { get; private set; }
    }
    public abstract partial class OneToOneFileTask : GenerateFileTask, ISingleSource
    {
        protected OneToOneFileTask(string relativePath)
            : base(relativePath)
        {
        }
        protected OneToOneFileTask(string source, string target, string state)
            : base(target, state)
        {
            Source = source;
        }

        public string Source
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue("source"); }
            set { TaskXml.SetAttributeValue("source", value); }
        }
    }
    public abstract partial class MultipleToOneFileTask : GenerateFileTask, IMultipleSources
    {
        protected MultipleToOneFileTask(string relativePath) : base(relativePath)
        {
        }
        protected MultipleToOneFileTask(IEnumerable<string> sources, string relativePath, string baseFolder,
                                        string state) : base(relativePath, state)
        {
            BaseFolder = baseFolder ?? string.Empty;
            Sources = sources;
        }

        public string BaseFolder
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue("baseFolder"); }
            set { TaskXml.SetAttributeValue("baseFolder", value); }
        }

        public string CurrentSource
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue("currentFile"); }
            set { TaskXml.SetAttributeValue("currentFile", value); }
        }

        public long? SourceCount
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue<long?>("sourceCount"); }
            set
            {
                if (value.HasValue) TaskXml.SetAttributeValue("sourceCount", value.Value);
                else TaskXml.SetAttributeValue("sourceCount", null);
            }
        }
        public long ProcessedSourceCount
        {
            get { return TaskXml == null ? 0 : TaskXml.GetAttributeValueWithDefault<long>("sourceProcessed"); }
            set { TaskXml.SetAttributeValue("sourceProcessed", value); }
        }

        public IEnumerable<string> Sources
        {
            get { return TaskXml.ElementsCaseInsensitive("source").Select(file => file.Value); }
            set
            {
                TaskXml.ElementsCaseInsensitive("source").Remove();
                foreach (var file in value) TaskXml.Add(new XElement("source", file));
            }
        }
    }

    public sealed partial class OfflineDownloadTask : GenerateFileTask, IRemoteTask
    {
        public OfflineDownloadTask(string relativePath) : base(relativePath)
        {
        }

        public string Url
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue("url"); }
            set { TaskXml.SetAttributeValue("url", AccountRemover.Replace(value, "ftp://")); }
        }

        public override long ProcessedFileLength
        {
            get
            {
                var file = new FileInfo(FileHelper.GetFilePath(RelativePath));
                return file.Exists ? file.Length : 0;
            }
            set { throw new NotSupportedException(); }
        }
    }
    public sealed partial class CompressTask : MultipleToOneFileTask
    {
        public CompressTask(string archiveFilePath) : base(archiveFilePath)
        {
        }
        public CompressTask(string archiveFilePath, IEnumerable<string> files, string baseFolder = null,
                            string compressionLevel = null) 
            : base(files.Select(file => FileHelper.Combine(baseFolder ?? string.Empty, file)),
                   archiveFilePath, baseFolder, TaskType.CompressTask)
        {
            TaskXml.SetAttributeValue("compressionLevel", compressionLevel ?? "Ultra");
        }
    }
    public sealed partial class ConvertTask : OneToOneFileTask
    {
        public ConvertTask(string relativePath) : base(relativePath)
        {
        }
        public ConvertTask(string source, string target, TimeSpan duration, string arguments = null)
            : base(source, target, TaskType.ConvertTask)
        {
            Duration = duration;
            Arguments = arguments;
        }

        public TimeSpan ProcessedDuration
        {
            get { return new TimeSpan(TaskXml.GetAttributeValueWithDefault<long>("durationProcessed")); }
            set { TaskXml.SetAttributeValue("durationProcessed", value.Ticks); }
        }
        public TimeSpan Duration
        {
            get { return new TimeSpan(TaskXml.GetAttributeValue<long>("duration")); }
            set { TaskXml.SetAttributeValue("duration", value.Ticks); }
        }
        public string Arguments
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue("arguments"); }
            set { TaskXml.SetAttributeValue("arguments", value); }
        }

        public override double? Percentage { get { return 100.0 * ProcessedDuration.Ticks / Duration.Ticks; } }
    }

    public abstract partial class GeneralTask : CloudTask
    {
        public static GeneralTask Create(string id)
        {
            switch (XHelper.Load(FileHelper.GetTaskPath(id)).Root.Name.LocalName.ToLowerInvariant())
            {
                case TaskType.FtpUploadTask:
                    return new FtpUploadTask(id);
                case TaskType.CrossAppCopyTask:
                    return new CrossAppCopyTask(id);
                case TaskType.DecompressTask:
                    return new DecompressTask(id);
                case TaskType.BitTorrentTask:
                    return new BitTorrentTask(id);
                default:
                    return null;
            }
        }
        protected GeneralTask(string id) : base(FileHelper.GetDataPath(id + ".task"))
        {
            ID = id;
        }
        protected GeneralTask(string type, bool create)
            : base(FileHelper.GetDataPath(DateTime.UtcNow.Shorten() + ".task"), type)
        {
            if (!create)
                throw new NotSupportedException("You should call .ctor(id) if you are NOT going to create something!");
            ID = Path.GetFileNameWithoutExtension(FilePath);
        }

        public string ID { get; private set; }
        public override sealed DateTime? StartTime
        {
            get { return Helper.Deshorten(ID); }
            set { throw new NotSupportedException(); }
        }

        public override sealed string Type { get { return TaskXml.Name.LocalName; } }
    }
    public abstract partial class MultipleFilesTask : GeneralTask
    {
        protected MultipleFilesTask(string id) : base(id)
        {
        }
        protected MultipleFilesTask(string type, string target) : base(type, true)
        {
            Target = target;
        }

        public virtual string CurrentFile
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue("currentFile"); }
            set { TaskXml.SetAttributeValue("currentFile", value); }
        }

        public virtual long? FileCount
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue<long?>("fileCount"); }
            set
            {
                if (value.HasValue) TaskXml.SetAttributeValue("fileCount", value.Value);
                else TaskXml.SetAttributeValue("fileCount", null);
            }
        }
        public virtual long ProcessedFileCount
        {
            get { return TaskXml == null ? 0 : TaskXml.GetAttributeValueWithDefault<long>("fileProcessed"); }
            set { TaskXml.SetAttributeValue("fileProcessed", value); }
        }

        public string Target
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue("target"); }
            set { TaskXml.SetAttributeValue("target", value); }
        }
    }
    public abstract partial class OneToMultipleFilesTask : MultipleFilesTask, ISingleSource
    {
        protected OneToMultipleFilesTask(string id) : base(id)
        {
        }
        protected OneToMultipleFilesTask(string type, string source, string target) : base(type, target)
        {
            Source = source;
        }

        public string Source
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue("source"); }
            set { TaskXml.SetAttributeValue("source", value); }
        }
    }

    public sealed partial class FtpUploadTask : GeneralTask, IRemoteTask, IMultipleSources
    {
        public FtpUploadTask(string id) : base(id)
        {
        }
        public FtpUploadTask(string baseFolder, IEnumerable<string> sources, string url)
            : base(TaskType.FtpUploadTask, true)
        {
            BaseFolder = baseFolder ?? string.Empty;
            Sources = sources;
            Url = url;
        }

        public string Url
        {
            get { return TaskXml == null ? null : AccountRemover.Replace(TaskXml.GetAttributeValue("url"), "ftp://"); }
            set { TaskXml.SetAttributeValue("url", value); }
        }
        internal string UrlFull { get { return TaskXml == null ? null : TaskXml.GetAttributeValue("url"); } }

        public string BaseFolder
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue("baseFolder"); }
            set { TaskXml.SetAttributeValue("baseFolder", value); }
        }

        public string CurrentSource
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue("currentFile"); }
            set { TaskXml.SetAttributeValue("currentFile", value); }
        }

        public long? SourceCount
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue<long?>("sourceCount"); }
            set
            {
                if (value.HasValue) TaskXml.SetAttributeValue("sourceCount", value.Value);
                else TaskXml.SetAttributeValue("sourceCount", null);
            }
        }
        public long ProcessedSourceCount
        {
            get { return TaskXml == null ? 0 : TaskXml.GetAttributeValueWithDefault<long>("sourceProcessed"); }
            set { TaskXml.SetAttributeValue("sourceProcessed", value); }
        }

        public IEnumerable<string> Sources
        {
            get { return TaskXml.ElementsCaseInsensitive("source").Select(file => file.Value); }
            set
            {
                TaskXml.ElementsCaseInsensitive("source").Remove();
                foreach (var file in value) TaskXml.Add(new XElement("source", file));
            }
        }
    }
    public sealed partial class CrossAppCopyTask : MultipleFilesTask
    {
        public CrossAppCopyTask(string id) : base(id)
        {
        }
        public CrossAppCopyTask(string domain, string source, string target) : base(TaskType.CrossAppCopyTask, target)
        {
            Domain = domain;
            Source = source;
        }

        public string Domain
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue("domain"); }
            set { TaskXml.SetAttributeValue("domain", value); }
        }
        public string Source
        {
            get { return TaskXml == null ? null : TaskXml.GetAttributeValue("source"); }
            set { TaskXml.SetAttributeValue("source", value); }
        }

        public override long? FileCount { get { return null; } set { throw new NotSupportedException(); } }
        public override long? FileLength { get { return null; } set { throw new NotSupportedException(); } }
    }
    public sealed partial class DecompressTask : OneToMultipleFilesTask
    {
        public DecompressTask(string id) : base(id)
        {
        }
        public DecompressTask(string source, string target) : base(TaskType.DecompressTask, source, target)
        {
        }
    }
    public sealed partial class BitTorrentTask : OneToMultipleFilesTask
    {
        public BitTorrentTask(string id) : base(id)
        {
        }
        public BitTorrentTask(string source, string target) : base(TaskType.BitTorrentTask, source, target)
        {
        }

        public override string CurrentFile { get { return null; } set { } }
        public override long ProcessedFileCount
        {
            get { return Status == TaskStatus.Done ? FileCount.HasValue ? FileCount.Value : 0 : 0; }
            set { throw new NotSupportedException(); }
        }
    }
}