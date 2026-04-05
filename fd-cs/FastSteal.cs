using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace fd_cs
{
    // Represents a stealable download range
    public class TaskItem
    {
        private readonly object _syncRoot = new object();
        public long Start { get; private set; }
        public long End { get; private set; }
        public long InitialStart { get; private set; }

        public TaskItem(long start, long end)
        {
            if (start > end) throw new ArgumentOutOfRangeException("Start cannot be > End");
            Start = start;
            End = end;
            InitialStart = start;
        }

        public long Remain
        {
            get
            {
                lock (_syncRoot) return End - Start;
            }
        }

        // Returns the span [oldStart, newStart) if successful, otherwise null
        public (long, long)? SafeAddStart(long start, long bias)
        {
            lock (_syncRoot)
            {
                if (Start != start) return null; // Stale start
                long newStart = Math.Min(start + bias, End);
                if (newStart <= Start) return null;
                
                long oldStart = Start;
                Start = newStart;
                return (oldStart, newStart);
            }
        }

        public (long, long)? SplitTwo()
        {
            lock (_syncRoot)
            {
                if (Start >= End) return null;
                long mid = Start + (End - Start) / 2;
                if (mid == Start) return null;
                
                long oldEnd = End;
                End = mid; // Shrink this task
                return (mid, oldEnd); // Return the stolen half
            }
        }

        public (long, long)? Take()
        {
            lock (_syncRoot)
            {
                if (Start == End) return null;
                long oldStart = Start;
                long oldEnd = End;
                Start = End;
                return (oldStart, oldEnd);
            }
        }
    }

    public class TaskQueue
    {
        private readonly object _syncRoot = new object();
        private readonly Queue<TaskItem> _waiting = new Queue<TaskItem>();
        private readonly List<TaskItem> _running = new List<TaskItem>();

        public TaskQueue(IEnumerable<(long start, long end)> initialTasks)
        {
            foreach (var (s, e) in initialTasks)
            {
                _waiting.Enqueue(new TaskItem(s, e));
            }
        }

        public void Add(long start, long end)
        {
            lock (_syncRoot)
            {
                _waiting.Enqueue(new TaskItem(start, end));
            }
        }
        
        public void RegisterWorker(TaskItem item)
        {
            lock (_syncRoot)
            {
                _running.Add(item);
            }
        }

        public void UnregisterWorker(TaskItem item)
        {
            lock (_syncRoot)
            {
                _running.Remove(item);
            }
        }

        public TaskItem? Steal(TaskItem currentWorkerTask, long minChunkSize)
        {
            lock (_syncRoot)
            {
                // Try to take from waiting queue
                while (_waiting.Count > 0)
                {
                    var task = _waiting.Dequeue();
                    var range = task.Take();
                    if (range.HasValue)
                    {
                        var newTask = new TaskItem(range.Value.Item1, range.Value.Item2);
                        return newTask;
                    }
                }

                // Try to steal from a running task
                var maxRemainTask = _running
                    .Where(t => t != currentWorkerTask)
                    .OrderByDescending(t => t.Remain)
                    .FirstOrDefault();

                if (maxRemainTask != null && maxRemainTask.Remain >= minChunkSize * 2)
                {
                    var stolen = maxRemainTask.SplitTwo();
                    if (stolen.HasValue)
                    {
                        var newTask = new TaskItem(stolen.Value.Item1, stolen.Value.Item2);
                        return newTask;
                    }
                }

                return null;
            }
        }
    }
}
