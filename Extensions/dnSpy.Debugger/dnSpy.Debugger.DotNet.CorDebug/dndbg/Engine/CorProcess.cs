﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using dndbg.COM.CorDebug;

namespace dndbg.Engine {
	sealed class CorProcess : COMObject<ICorDebugProcess>, IEquatable<CorProcess> {
		/// <summary>
		/// Returns the value of ICorDebugProcess::GetHelperThreadID(). Don't cache this value since
		/// it can change. 0 is returned if the thread doesn't exist.
		/// </summary>
		public uint HelperThreadId {
			get {
				int hr = obj.GetHelperThreadID(out uint threadId);
				return hr < 0 ? 0 : threadId;
			}
		}

		/// <summary>
		/// Gets the process id (pid) of the process
		/// </summary>
		public int ProcessId => pid;
		readonly int pid;

		/// <summary>
		/// true if the threads are running freely
		/// </summary>
		public bool IsRunning {
			get {
				int hr = obj.IsRunning(out int running);
				return hr >= 0 && running != 0;
			}
		}

		/// <summary>
		/// Gets all threads
		/// </summary>
		public IEnumerable<CorThread> Threads {
			get {
				int hr = obj.EnumerateThreads(out var threadEnum);
				if (hr < 0)
					yield break;
				for (;;) {
					hr = threadEnum.Next(1, out var thread, out uint count);
					if (hr != 0 || thread == null)
						break;
					yield return new CorThread(thread);
				}
			}
		}

		/// <summary>
		/// Gets all AppDomains
		/// </summary>
		public IEnumerable<CorAppDomain> AppDomains {
			get {
				int hr = obj.EnumerateAppDomains(out var appDomainEnum);
				if (hr < 0)
					yield break;
				for (;;) {
					hr = appDomainEnum.Next(1, out var appDomain, out uint count);
					if (hr != 0 || appDomain == null)
						break;
					yield return new CorAppDomain(appDomain);
				}
			}
		}

		/// <summary>
		/// Gets the process handle. It's owned by the CLR debugger
		/// </summary>
		public IntPtr Handle {
			get {
				int hr = obj.GetHandle(out var handle);
				return hr < 0 ? IntPtr.Zero : handle;
			}
		}

		/// <summary>
		/// Gets the CLR version
		/// </summary>
		public Version CLRVersion {
			get {
				if (obj is ICorDebugProcess2 p2) {
					int hr = p2.GetVersion(out var ver);
					if (hr >= 0)
						return new Version((int)ver.dwMajor, (int)ver.dwMinor, (int)ver.dwBuild, (int)ver.dwSubBuild);
				}
				return new Version(0, 0, 0, 0);
			}
		}

		/// <summary>
		/// Gets/sets desired NGEN compiler flags. The setter can only be called in a
		/// ICorDebugManagedCallback::CreateProcess() handler
		/// </summary>
		public CorDebugJITCompilerFlags DesiredNGENCompilerFlags {
			get {
				var p2 = obj as ICorDebugProcess2;
				if (p2 == null)
					return 0;
				int hr = p2.GetDesiredNGENCompilerFlags(out var flags);
				return hr < 0 ? 0 : flags;
			}
			set {
				var p2 = obj as ICorDebugProcess2;
				if (p2 == null)
					return;
				int hr = p2.SetDesiredNGENCompilerFlags(value);
			}
		}

		public CorProcess(ICorDebugProcess process)
			: base(process) {
			int hr = process.GetID(out pid);
			if (hr < 0)
				pid = 0;

			//TODO: ICorDebugProcess2::GetReferenceValueFromGCHandle
			//TODO: ICorDebugProcess5 (GC methods)
		}

		/// <summary>
		/// Reads memory. Returns a HRESULT.
		/// </summary>
		/// <param name="address">Address</param>
		/// <param name="buffer">Buffer</param>
		/// <param name="index">Index into <paramref name="buffer"/></param>
		/// <param name="size">Size to read</param>
		/// <param name="sizeRead">Number of bytes read</param>
		/// <returns></returns>
		public unsafe int ReadMemory(ulong address, byte[] buffer, long index, int size, out int sizeRead) {
			IntPtr sizeRead2 = IntPtr.Zero;
			int hr;
			fixed (byte* p = &buffer[index])
				hr = obj.ReadMemory(address, (uint)size, new IntPtr(p), out sizeRead2);
			const int ERROR_PARTIAL_COPY = unchecked((int)0x8007012B);
			if (hr < 0 && hr != ERROR_PARTIAL_COPY) {
				sizeRead = 0;
				return hr;
			}

			sizeRead = (int)sizeRead2.ToInt64();
			return 0;
		}

		/// <summary>
		/// Writes memory. Returns a HRESULT.
		/// </summary>
		/// <param name="address">Address</param>
		/// <param name="buffer">Buffer</param>
		/// <param name="index">Index into <paramref name="buffer"/></param>
		/// <param name="size">Size to write</param>
		/// <param name="sizeWritten">Number of bytes written</param>
		/// <returns></returns>
		public unsafe int WriteMemory(ulong address, byte[] buffer, long index, int size, out int sizeWritten) {
			if (size == 0) {
				sizeWritten = 0;
				return 0;
			}
			fixed (byte* p = &buffer[index])
				return WriteMemory(address, p, size, out sizeWritten);
		}

		public unsafe int WriteMemory(ulong address, void* buffer, int size, out int sizeWritten) {
			var sizeWritten2 = IntPtr.Zero;
			int hr = obj.WriteMemory(address, (uint)size, new IntPtr(buffer), out sizeWritten2);
			const int ERROR_PARTIAL_COPY = unchecked((int)0x8007012B);
			if (hr < 0 && hr != ERROR_PARTIAL_COPY) {
				sizeWritten = 0;
				return hr;
			}

			sizeWritten = (int)sizeWritten2.ToInt64();
			return 0;
		}

		/// <summary>
		/// Reads memory from the debugged process. Returns null if we failed to read all bytes
		/// or if <paramref name="addr"/> is null
		/// </summary>
		/// <param name="addr">Address</param>
		/// <param name="size">Size</param>
		/// <returns></returns>
		public byte[] ReadMemory(ulong addr, int size) {
			if (addr == 0 || size < 0)
				return null;
			var buf = new byte[size];
			for (int index = 0; index < size;) {
				int sizeLeft = size - index;
				int hr = ReadMemory(addr, buf, index, sizeLeft, out int sizeRead);
				if (hr < 0 || sizeRead <= 0)
					return null;
				index += sizeRead;
				addr += (ulong)sizeRead;
			}
			return buf;
		}

		/// <summary>
		/// Sets the debug state of all managed threads
		/// </summary>
		/// <param name="state">New state</param>
		/// <param name="thread">Thread to exempt from the new state or null</param>
		public void SetAllThreadsDebugState(CorDebugThreadState state, CorThread thread = null) =>
			obj.SetAllThreadsDebugState(state, thread?.RawObject);

		/// <summary>
		/// true if any managed callbacks are currently queued for the specified thread
		/// </summary>
		/// <param name="thread">Thread or null to check all threads</param>
		/// <returns></returns>
		public bool HasQueuedCallbacks(CorThread thread) {
			int hr = obj.HasQueuedCallbacks(thread?.RawObject, out int queued);
			return hr >= 0 && queued != 0;
		}

		/// <summary>
		/// Gets a thread or null
		/// </summary>
		/// <param name="threadId">Thread ID</param>
		/// <returns></returns>
		public CorThread GetThread(uint threadId) {
			int hr = obj.GetThread(threadId, out var thread);
			return hr < 0 || thread == null ? null : new CorThread(thread);
		}

		/// <summary>
		/// true if the specified thread has been suspended as a result of the debugger stopping this process
		/// </summary>
		/// <param name="threadId">Thread id</param>
		/// <returns></returns>
		public bool IsOSSuspended(uint threadId) {
			int hr = obj.IsOSSuspended(threadId, out int susp);
			return hr >= 0 && susp != 0;
		}

		public void SetEnableCustomNotification(CorClass cls, bool enable) {
			if (obj is ICorDebugProcess3 p3) {
				int hr = p3.SetEnableCustomNotification(cls.RawObject, enable ? 1 : 0);
			}
		}

		public void SetWriteableMetadataUpdateMode(WriteableMetadataUpdateMode mode) {
			if (obj is ICorDebugProcess7 p7) {
				int hr = p7.SetWriteableMetadataUpdateMode(mode);
				// 0x80131c4e: CORDBG_E_UNSUPPORTED
				// Not supported in V2 debuggers (when shim is used). Supported in V3, which we're
				// not using.
			}
		}

		public void EnableLogMessages(bool enable) => obj.EnableLogMessages(enable ? 1 : 0);

		public void EnableExceptionCallbacksOutsideOfMyCode(bool value) {
			if (obj is ICorDebugProcess8 p8) {
				int hr = p8.EnableExceptionCallbacksOutsideOfMyCode(value ? 1 : 0);
			}
		}

		public void EnableNGENPolicy(CorDebugNGENPolicy policy) {
			if (obj is ICorDebugProcess5 p5) {
				int hr = p5.EnableNGENPolicy(policy);
			}
		}

		public bool Terminate(int exitCode) => obj.Terminate((uint)exitCode) >= 0;

		public bool Detach() {
			int hr = obj.Detach();
			return hr >= 0;
		}

		public bool IsTransitionStub(ulong addr) {
			int hr = obj.IsTransitionStub(addr, out int ts);
			return hr >= 0 && ts != 0;
		}

		public void ClearCurrentException(uint threadId) => obj.ClearCurrentException(threadId);

		public CorThread ThreadForFiberCookie(uint fiberCookie) {
			int hr = obj.ThreadForFiberCookie(fiberCookie, out var thread);
			return hr < 0 || thread == null ? null : new CorThread(thread);
		}

		/// <summary>
		/// Converts an object address to a <see cref="CorValue"/>. Return value is null or
		/// a <see cref="ICorDebugObjectValue"/>.
		/// </summary>
		/// <param name="address">Address of object</param>
		/// <returns></returns>
		public CorValue GetObject(ulong address) {
			var p5 = obj as ICorDebugProcess5;
			if (p5 == null)
				return null;
			int hr = p5.GetObject(address, out var value);
			return hr < 0 || value == null ? null : new CorValue(value);
		}

		public static bool operator ==(CorProcess a, CorProcess b) {
			if (ReferenceEquals(a, b))
				return true;
			if (ReferenceEquals(a, null) || ReferenceEquals(b, null))
				return false;
			return a.Equals(b);
		}

		public static bool operator !=(CorProcess a, CorProcess b) => !(a == b);

		public bool Equals(CorProcess other) => !ReferenceEquals(other, null) && RawObject == other.RawObject;
		public override bool Equals(object obj) => Equals(obj as CorProcess);
		public override int GetHashCode() => RawObject.GetHashCode();
		public override string ToString() => string.Format("[Process] {0} CLR v{1} Flags={2}", ProcessId, CLRVersion, DesiredNGENCompilerFlags);
	}
}
