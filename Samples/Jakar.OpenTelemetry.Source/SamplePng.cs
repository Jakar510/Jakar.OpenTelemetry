using System.Buffers.Binary;
using System.IO.Compression;

namespace Jakar.OpenTelemetry.Source;

/// <summary> Tiny dependency-free PNG encoder used to fabricate "screenshots" for the simulated errors. </summary>
internal static class SamplePng
{
	private static readonly uint[] CrcTable = CreateCrcTable();

	public static byte[] Create( int width, int height, int seed )
	{
		int    stride = width           * 3 + 1;
		byte[] pixels = new byte[stride * height];

		for ( int y = 0; y < height; y++ )
		{
			int row = y * stride; // pixels[row] = 0: no filter

			for ( int x = 0; x < width; x++ )
			{
				int  offset = row + 1 + x               * 3;
				bool stripe = ( ( x + y + seed ) / 24 ) % 2 == 0;
				pixels[offset] = (byte)( stripe
											 ? 255
											 : 120 + seed * 17 % 120 );
				pixels[offset + 1] = (byte)( 40 + y * 180 / height );
				pixels[offset + 2] = (byte)( 60 + x * 160 / width );
			}
		}

		using MemoryStream png = new();
		png.Write( [ 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A ] );

		byte[] header = new byte[13];
		BinaryPrimitives.WriteInt32BigEndian( header,             width );
		BinaryPrimitives.WriteInt32BigEndian( header.AsSpan( 4 ), height );
		header[8] = 8; // bit depth
		header[9] = 2; // RGB
		WriteChunk( png, "IHDR"u8, header );

		using ( MemoryStream compressed = new() )
		{
			using ( ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true) ) { zlib.Write( pixels ); }

			WriteChunk( png, "IDAT"u8, compressed.ToArray() );
		}

		WriteChunk( png, "IEND"u8, [ ] );
		return png.ToArray();
	}

	private static void WriteChunk( Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data )
	{
		Span<byte> buffer = stackalloc byte[4];
		BinaryPrimitives.WriteInt32BigEndian( buffer, data.Length );
		stream.Write( buffer );
		stream.Write( type );
		stream.Write( data );

		uint crc = Crc( 0xFFFFFFFFu, type );
		crc = Crc( crc, data ) ^ 0xFFFFFFFFu;
		BinaryPrimitives.WriteUInt32BigEndian( buffer, crc );
		stream.Write( buffer );
	}

	private static uint Crc( uint crc, ReadOnlySpan<byte> data )
	{
		foreach ( byte b in data ) { crc = CrcTable[( crc ^ b ) & 0xFF] ^ ( crc >> 8 ); }

		return crc;
	}

	private static uint[] CreateCrcTable()
	{
		uint[] table = new uint[256];

		for ( uint n = 0; n < 256; n++ )
		{
			uint c = n;
			for ( int k = 0; k < 8; k++ )
			{
				c = ( c & 1 ) != 0
						? 0xEDB88320u ^ ( c >> 1 )
						: c >> 1;
			}

			table[n] = c;
		}

		return table;
	}
}
