// Copyright (c) 2010-2014 SharpDX - Alexandre Mutel
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace SharpGen.Runtime.Diagnostics
{
    /// <summary>
    /// Event args for <see cref="CppObject"/> used by <see cref="ObjectTracker"/>.
    /// </summary>
    public class CppObjectEventArgs : EventArgs
    {
        /// <summary>
        /// The object being tracked/untracked.
        /// </summary>
        public CppObject Object { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CppObjectEventArgs"/> class.
        /// </summary>
        /// <param name="o">The o.</param>
        public CppObjectEventArgs(CppObject o)
        {
            Object = o;
        }
    }


    /// <summary>
    /// Track all allocated objects.
    /// </summary>
    public static class ObjectTracker
    {
        private static Dictionary<IntPtr, List<ObjectReference>> _processGlobalObjectReferences;

        private static readonly ThreadLocal<Dictionary<IntPtr, List<ObjectReference>>> ThreadStaticObjectReferences = new(static () => new Dictionary<IntPtr, List<ObjectReference>>(), false);

        /// <summary>
        /// Occurs when a CppObject is tracked.
        /// </summary>
        public static event EventHandler<CppObjectEventArgs> Tracked;

        /// <summary>
        /// Occurs when a CppObject is untracked.
        /// </summary>
        public static event EventHandler<CppObjectEventArgs> UnTracked;

        /// <summary>
        /// Function which provides stack trace for object tracking.
        /// </summary>
        public static Func<string> StackTraceProvider { get; set; } = GetStackTrace;

        private static Dictionary<IntPtr, List<ObjectReference>> ObjectReferences =>
            Configuration.UseThreadStaticObjectTracking
                ? ThreadStaticObjectReferences.Value
                : _processGlobalObjectReferences ??= new Dictionary<IntPtr, List<ObjectReference>>();

        /// <summary>
        /// Gets default stack trace.
        /// </summary>
        public static string GetStackTrace()
        {
#if NETSTANDARD1_1
            try
            {
                throw new GetStackTraceException();
            }
            catch (GetStackTraceException ex)
            {
                return ex.StackTrace;
            }
#else
            return new StackTrace(1).ToString();
#endif
        }

        /// <summary>
        /// Tracks the specified C++ object.
        /// </summary>
        /// <param name="cppObject">The C++ object.</param>
        public static void Track(CppObject cppObject)
        {
            if (cppObject == null)
                return;

            var nativePointer = cppObject.NativePointer;
            if (nativePointer == IntPtr.Zero)
                return;

            lock (ObjectReferences)
            {
                if (!ObjectReferences.TryGetValue(nativePointer, out var referenceList))
                {
                    referenceList = new List<ObjectReference>();
                    ObjectReferences.Add(nativePointer, referenceList);
                }

                referenceList.Add(new ObjectReference(DateTime.Now, cppObject, StackTraceProvider != null ? StackTraceProvider() : string.Empty));
            }

            // Fire Tracked event.
            OnTracked(cppObject);
        }

        /// <summary>
        /// Finds a list of object reference from a specified C++ object pointer.
        /// </summary>
        /// <param name="comObjectPtr">The C++ object pointer.</param>
        /// <returns>A list of object reference</returns>
        public static List<ObjectReference> Find(IntPtr objPtr)
        {
            lock (ObjectReferences)
            {
                // Object is already tracked
                if (ObjectReferences.TryGetValue(objPtr, out var referenceList))
                    return new List<ObjectReference>(referenceList);
            }
            return new List<ObjectReference>();
        }

        /// <summary>
        /// Finds the object reference for a specific COM object.
        /// </summary>
        /// <param name="cppObject">The COM object.</param>
        /// <returns>An object reference</returns>
        public static ObjectReference Find(CppObject cppObject)
        {
            lock (ObjectReferences)
            {
                if (ObjectReferences.TryGetValue(cppObject.NativePointer, out var referenceList))
                {
                    foreach (var objectReference in referenceList)
                    {
                        if (ReferenceEquals(objectReference.Object.Target, cppObject))
                            return objectReference;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Untracks the specified COM object.
        /// </summary>
        /// <param name="cppObject">The COM object.</param>
        public static void UnTrack(CppObject cppObject)
        {
            if (cppObject == null || cppObject.NativePointer == IntPtr.Zero)
                return;

            bool foundTracked;
            lock (ObjectReferences)
            {
                // Object is already tracked
                foundTracked = ObjectReferences.TryGetValue(cppObject.NativePointer, out var referenceList);
                if (foundTracked)
                {
                    for (int i = referenceList.Count-1; i >=0; i--)
                    {
                        var objectReference = referenceList[i];
                        if (ReferenceEquals(objectReference.Object.Target, cppObject))
                            referenceList.RemoveAt(i);
                        else if (!objectReference.IsAlive)
                            referenceList.RemoveAt(i);
                    }
                    // Remove empty list
                    if (referenceList.Count == 0)
                        ObjectReferences.Remove(cppObject.NativePointer);
                }
            }

            if (foundTracked)
            {
                // Fire UnTracked event
                OnUnTracked(cppObject);
            }
        }

        /// <summary>
        /// Reports all COM object that are active and not yet disposed.
        /// </summary>
        public static List<ObjectReference> FindActiveObjects()
        {
            var activeObjects = new List<ObjectReference>();
            lock (ObjectReferences)
            {
                foreach (var referenceList in ObjectReferences.Values)
                {
                    foreach (var objectReference in referenceList)
                    {
                        if (objectReference.IsAlive)
                            activeObjects.Add(objectReference);
                    }
                }
            }
            return activeObjects;
        }

        /// <summary>
        /// Reports all C++ objects that are active and not yet disposed.
        /// </summary>
        public static string ReportActiveObjects()
        {
            var text = new StringBuilder();
            var count = 0;
            var countPerType = new Dictionary<string, int>();

            foreach (var findActiveObject in FindActiveObjects())
            {
                var findActiveObjectStr = findActiveObject.ToString();
                if (string.IsNullOrEmpty(findActiveObjectStr))
                    continue;

                text.AppendFormat("[{0}]: {1}", count++, findActiveObjectStr);

                var target = findActiveObject.Object.Target;
                if (target == null)
                    continue;

                var targetType = target.GetType().Name;
                countPerType[targetType] = countPerType.TryGetValue(targetType, out var typeCount)
                                               ? typeCount + 1
                                               : 1;
            }

            var keys = new List<string>(countPerType.Keys);
            keys.Sort();

            text.AppendLine();
            text.AppendLine("Count per Type:");
            foreach (var key in keys)
            {
                text.AppendFormat("{0} : {1}", key, countPerType[key]);
                text.AppendLine();
            }

            return text.ToString();
        }

        private static void OnTracked(CppObject obj)
        {
            Tracked?.Invoke(null, new CppObjectEventArgs(obj));
        }

        private static void OnUnTracked(CppObject obj)
        {
            UnTracked?.Invoke(null, new CppObjectEventArgs(obj));
        }

#if NETSTANDARD1_1
        private class GetStackTraceException : Exception
        {
        }
#endif
   }
}