﻿using System;
using System.Collections.Generic;
using System.IO;
using dot10.IO;
using dot10.PE;
using dot10.DotNet.MD;

namespace dot10.DotNet.Writer {
	/// <summary>
	/// .NET Heap interface
	/// </summary>
	interface IHeap : IChunk {
		/// <summary>
		/// Gets the name of the heap
		/// </summary>
		string Name { get; }

		/// <summary>
		/// Checks whether the heap is empty
		/// </summary>
		bool IsEmpty { get; }
	}

	/// <summary>
	/// Base class of most heaps
	/// </summary>
	abstract class HeapBase : IHeap {
		FileOffset offset;
		RVA rva;

		/// <inheritdoc/>
		public abstract string Name { get; }

		/// <inheritdoc/>
		public bool IsEmpty {
			get { return GetLength() <= 1; }
		}

		/// <inheritdoc/>
		public void SetOffset(FileOffset offset, RVA rva) {
			this.offset = offset;
			this.rva = rva;
		}

		/// <inheritdoc/>
		public abstract uint GetLength();

		/// <inheritdoc/>
		public abstract void WriteTo(BinaryWriter writer);
	}

	/// <summary>
	/// #Strings heap
	/// </summary>
	sealed class StringsHeap : HeapBase {
		Dictionary<UTF8String, uint> cachedDict = new Dictionary<UTF8String, uint>(UTF8StringEqualityComparer.Instance);
		List<UTF8String> cached = new List<UTF8String>();
		uint nextOffset = 1;

		/// <inheritdoc/>
		public override string Name {
			get { return "#Strings"; }
		}

		/// <summary>
		/// Adds a string to the #Strings heap
		/// </summary>
		/// <param name="s">The string</param>
		/// <returns>The offset of the string in the #Strings heap</returns>
		public uint Add(UTF8String s) {
			if (UTF8String.IsNullOrEmpty(s))
				return 0;

			uint offset;
			if (cachedDict.TryGetValue(s, out offset))
				return offset;

			if (Array.IndexOf(s.Data, 0) >= 0)
				throw new ArgumentException("Strings in the #Strings heap can't contain 00h bytes");

			cached.Add(s);
			cachedDict[s] = offset = nextOffset;
			nextOffset += (uint)s.Data.Length + 1;
			return offset;
		}

		/// <inheritdoc/>
		public override uint GetLength() {
			return nextOffset;
		}

		/// <inheritdoc/>
		public override void WriteTo(BinaryWriter writer) {
			writer.Write((byte)0);
			foreach (var s in cached) {
				writer.Write(s.Data);
				writer.Write((byte)0);
			}
		}
	}

	/// <summary>
	/// #US heap
	/// </summary>
	sealed class USHeap : HeapBase {
		Dictionary<string, uint> cachedDict = new Dictionary<string, uint>(StringComparer.Ordinal);
		List<string> cached = new List<string>();
		uint nextOffset = 1;

		/// <inheritdoc/>
		public override string Name {
			get { return "#US"; }
		}

		/// <summary>
		/// Adds a string to the #US heap
		/// </summary>
		/// <param name="s">The string</param>
		/// <returns>The offset of the string in the #US heap</returns>
		public uint Add(string s) {
			if (s == null || s.Length == 0)
				return 0;

			uint offset;
			if (cachedDict.TryGetValue(s, out offset))
				return offset;

			cached.Add(s);
			cachedDict[s] = offset = nextOffset;
			nextOffset += (uint)(Utils.GetCompressedUInt32Length((uint)s.Length) + s.Length * 2 + 1);
			return offset;
		}

		/// <inheritdoc/>
		public override uint GetLength() {
			return nextOffset;
		}

		/// <inheritdoc/>
		public override void WriteTo(BinaryWriter writer) {
			writer.Write((byte)0);
			foreach (var s in cached) {
				writer.WriteCompressedUInt32((uint)s.Length * 2 + 1);
				byte last = 0;
				for (int i = 0; i < s.Length; i++) {
					ushort c = (ushort)s[i];
					writer.Write(c);
					if (c > 0xFF || (1 <= c && c <= 8) || (0x0E <= c && c <= 0x1F) || c == 0x27 || c == 0x2D || c == 0x7F)
						last = 1;
				}
				writer.Write(last);
			}
		}
	}

	/// <summary>
	/// #Blob heap
	/// </summary>
	sealed class BlobHeap : HeapBase {
		Dictionary<byte[], uint> cachedDict = new Dictionary<byte[], uint>(ByteArrayEqualityComparer.Instance);
		List<byte[]> cached = new List<byte[]>();
		uint nextOffset = 1;

		/// <inheritdoc/>
		public override string Name {
			get { return "#Blob"; }
		}

		/// <summary>
		/// Adds data to the #Blob heap
		/// </summary>
		/// <param name="data">The data</param>
		/// <returns>The offset of the data in the #Blob heap</returns>
		public uint Add(byte[] data) {
			if (data == null || data.Length == 0)
				return 0;

			uint offset;
			if (cachedDict.TryGetValue(data, out offset))
				return offset;

			cached.Add(data);
			cachedDict[data] = offset = nextOffset;
			nextOffset += (uint)(Utils.GetCompressedUInt32Length((uint)data.Length) + data.Length);
			return offset;
		}

		/// <inheritdoc/>
		public override uint GetLength() {
			return nextOffset;
		}

		/// <inheritdoc/>
		public override void WriteTo(BinaryWriter writer) {
			writer.Write((byte)0);
			foreach (var data in cached) {
				writer.WriteCompressedUInt32((uint)data.Length);
				writer.Write(data);
			}
		}
	}

	/// <summary>
	/// #GUID heap
	/// </summary>
	sealed class GuidHeap : HeapBase {
		List<Guid> guids = new List<Guid>();

		/// <inheritdoc/>
		public override string Name {
			get { return "#GUID"; }
		}

		/// <summary>
		/// Adds a guid to the #GUID heap
		/// </summary>
		/// <param name="guid">The guid</param>
		/// <returns>The index of the guid in the #GUID heap</returns>
		public uint Add(Guid? guid) {
			if (guid == null)
				return 0;

			// The number of GUIDs will almost always be 1 so there's no need for a dictionary.
			// The only table that contains GUIDs is the Module table, and it has three GUID
			// columns. Only one of them (Mvid) is normally set and the others are null.
			int index = guids.IndexOf(guid.Value);
			if (index >= 0)
				return (uint)index + 1;

			guids.Add(guid.Value);
			return (uint)guids.Count;
		}

		/// <inheritdoc/>
		public override uint GetLength() {
			return (uint)guids.Count * 16;
		}

		/// <inheritdoc/>
		public override void WriteTo(BinaryWriter writer) {
			foreach (var guid in guids)
				writer.Write(guid.ToByteArray());
		}
	}
}
