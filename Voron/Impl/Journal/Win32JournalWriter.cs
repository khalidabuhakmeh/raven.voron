﻿using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Voron.Impl.Journal
{
	public unsafe class Win32FileJournalWriter: IJournalWriter
	{
		private readonly string filename;
		private readonly SafeFileHandle _handle;

		[DllImport("kernel32.dll")]
		static extern bool WriteFileGather(
			SafeFileHandle hFile,
			FileSegmentElement* aSegmentArray,
			uint nNumberOfBytesToWrite,
			IntPtr lpReserved,
			NativeOverlapped* lpOverlapped);


		[StructLayout(LayoutKind.Explicit, Size = 8)]
		public struct FileSegmentElement
		{
			[FieldOffset(0)]
			public byte* Buffer;
			[FieldOffset(0)]
			public UInt64 Alignment;
		}

		public Win32FileJournalWriter(string filename, long journalSize)
		{
			this.filename = filename;
			_handle = NativeFileMethods.CreateFile(filename,
				NativeFileAccess.GenericWrite | NativeFileAccess.GenericWrite, NativeFileShare.None, IntPtr.Zero,
				NativeFileCreationDisposition.OpenAlways,
				NativeFileAttributes.Write_Through | NativeFileAttributes.NoBuffering | NativeFileAttributes.Overlapped, IntPtr.Zero);

			if (_handle.IsInvalid)
				throw new Win32Exception();

			NativeFileMethods.SetFileLength(_handle, journalSize);

			NumberOfAllocatedPages = journalSize/AbstractPager.PageSize;

			if (ThreadPool.BindHandle(_handle) == false)
				throw new InvalidOperationException("Could not bind the handle to the thread pool");
		}

		private const int ErrorIOPending = 997;
		private const int ErrorSuccess = 0;
		private const int ErrorOperationAborted = 995;

		public Task WriteGatherAsync(long position, byte*[] pages)
		{
			if (Disposed)
				throw new ObjectDisposedException("Win32JournalWriter");

			var tcs = new TaskCompletionSource<object>();
			var mre = new ManualResetEvent(false);
			var allocHGlobal = Marshal.AllocHGlobal(sizeof(FileSegmentElement) * (pages.Length + 1));
			var nativeOverlapped = CreateNativeOverlapped(position, tcs, allocHGlobal, mre);
			var array = (FileSegmentElement*)allocHGlobal.ToPointer();
			for (int i = 0; i < pages.Length; i++)
			{
				array[i].Buffer = pages[i];
			}
			array[pages.Length].Buffer = null;// null terminating

			WriteFileGather(_handle, array, (uint)pages.Length * 4096, IntPtr.Zero, nativeOverlapped);
			return HandleResponse(false, nativeOverlapped, tcs, allocHGlobal);
		}

		public long NumberOfAllocatedPages { get; private set; }
		
		public IVirtualPager CreatePager()
		{
			return new MemoryMapPager(filename);
		}

		public Task WriteAsync(long position, byte* ptr, int length)
		{
			if (Disposed)
				throw new ObjectDisposedException("Win32JournalWriter");

			var tcs = new TaskCompletionSource<object>();

			var nativeOverlapped = CreateNativeOverlapped(position, tcs, IntPtr.Zero, null);

			int written;
			var result = NativeFileMethods.WriteFile(_handle, ptr, length, out written, nativeOverlapped);

			return HandleResponse(result, nativeOverlapped, tcs, IntPtr.Zero);
		}

		private static Task HandleResponse(bool completedSyncronously, NativeOverlapped* nativeOverlapped, TaskCompletionSource<object> tcs, IntPtr memoryToFree)
		{
			if (completedSyncronously)
			{
				Overlapped.Free(nativeOverlapped);
				if (memoryToFree != IntPtr.Zero)
					Marshal.FreeHGlobal(memoryToFree);
				tcs.SetResult(null);
				return tcs.Task;
			}

			var lastWin32Error = Marshal.GetLastWin32Error();
			if (lastWin32Error == ErrorIOPending || lastWin32Error == ErrorSuccess)
				return tcs.Task;

			Overlapped.Free(nativeOverlapped);
			if (memoryToFree != IntPtr.Zero)
				Marshal.FreeHGlobal(memoryToFree);
			throw new Win32Exception(lastWin32Error);
		}

		private static NativeOverlapped* CreateNativeOverlapped(long position, TaskCompletionSource<object> tcs, IntPtr memoryToFree, ManualResetEvent manualResetEvent)
		{
			var o = new Overlapped((int)(position & 0xffffffff), (int)(position >> 32), manualResetEvent.SafeWaitHandle.DangerousGetHandle(), null);
			var nativeOverlapped = o.Pack((code, bytes, overlap) =>
			{
				try
				{
					switch (code)
					{
						case ErrorSuccess:
							tcs.TrySetResult(null);
							break;
						case ErrorOperationAborted:
							tcs.TrySetCanceled();
							break;
						default:
							tcs.TrySetException(new Win32Exception((int)code));
							break;
					}
				}
				finally
				{
					Overlapped.Free(overlap);
					if (memoryToFree != IntPtr.Zero)
						Marshal.FreeHGlobal(memoryToFree);
				}
			}, null);
			return nativeOverlapped;
		}

		public void Dispose()
		{
			Disposed = true;
			GC.SuppressFinalize(this);
			_handle.Close();
		}

		public bool Disposed { get; private set; }

		~Win32FileJournalWriter()
		{
			_handle.Close();
		}
	}
}