namespace Voron.Impl.Scratch
{
	public class PageFromScratchBuffer
	{
		public int ScratchFileNumber;
		public long PositionInScratchBuffer;
		public long Size;
		public int NumberOfPages;

		public override bool Equals(object obj)
		{
			if (ReferenceEquals(null, obj)) return false;
			if (ReferenceEquals(this, obj)) return true;
			if (obj.GetType() != this.GetType()) return false;

			var other = (PageFromScratchBuffer)obj;

			return PositionInScratchBuffer == other.PositionInScratchBuffer && Size == other.Size && NumberOfPages == other.NumberOfPages;
		}

		public override int GetHashCode()
		{
			unchecked
			{
				var hashCode = PositionInScratchBuffer.GetHashCode();
				hashCode = (hashCode * 397) ^ Size.GetHashCode();
				hashCode = (hashCode * 397) ^ NumberOfPages;
				return hashCode;
			}
		}

		public override string ToString()
		{
			return string.Format("PositionInScratchBuffer: {0}, Size: {1}, NumberOfPages: {2}", PositionInScratchBuffer, Size, NumberOfPages);
		}
	}
}