﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nevar.Trees;

namespace Nevar.Impl
{
	public unsafe class PureMemoryPager : IVirtualPager
	{
		private readonly List<MemoryHandle> _handles = new List<MemoryHandle>();

		private const int SegmentSize = Constants.PageSize * 1024;

		private void AddSegment()
		{
			var ptr = Marshal.AllocHGlobal(SegmentSize);
			_handles.Add(new MemoryHandle
				{
					Base = (byte*) ptr.ToPointer(),
					Ptr = ptr
				});
		}

		public class MemoryHandle
		{
			public IntPtr Ptr;
			public byte* Base;
		}

		public void Dispose()
		{
			foreach (var memoryHandle in _handles)
			{
				Marshal.FreeHGlobal(memoryHandle.Ptr);
			}
		}

		public Page Get(int n)
		{
			var index = n/SegmentSize;
			if (index == _handles.Count)
			{
				AddSegment();
			}
			var pageStart = _handles[index].Base + (n%SegmentSize*Constants.PageSize);
			return new Page(pageStart);
		}
	}
}