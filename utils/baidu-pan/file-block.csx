#load "../md5.csx"

/// <summary>
/// 返回文件的分片。
/// </summary>
static async Task<FileBlock[]> GetFileBlocks(string filePath, int blockSize)
{
	var fileSize = new FileInfo(filePath).Length;
	int blockCount = (int)Math.Ceiling((double)fileSize / blockSize);
	FileBlock[] blocks = new FileBlock[blockCount];
	// 计算文件分片。
	using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read);
	byte[] buffer = new byte[blockSize];
	int offset = 0;
	for (int i = 0; i < blockCount; i++, offset += blockSize)
	{
		int size = await fs.ReadAsync(buffer);
		string md5 = CalculateMD5(buffer.AsSpan(0, size));
		blocks[i] = new FileBlock(offset, size, md5);
	}
	return blocks;
}

/// <summary>
/// 文件的分片。
/// </summary>
record class FileBlock(int Offset, int Size, string MD5) { }
