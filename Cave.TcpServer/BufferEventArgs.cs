using System;

namespace Cave.Net
{
    /// <summary>
    /// Provides buffer information
    /// </summary>
    public class BufferEventArgs : EventArgs
    {
        bool handled;

        /// <summary>
        /// Full buffer instance
        /// </summary>
        public byte[] Buffer { get; }

        /// <summary>
        /// Start offset of data in <see cref="Buffer"/>
        /// </summary>
        public int Offset { get; }

        /// <summary>
        /// Length of data in <see cref="Buffer"/>
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// Set this to true to tell the sender that the buffer has been handled. Further processing will be skipped.
        /// </summary>
        public bool Handled { get => handled; set => handled |= value; }

        /// <summary>
        /// Creates a new <see cref="BufferEventArgs"/> instance
        /// </summary>
        /// <param name="buffer">buffer instance</param>
        /// <param name="offset">offset of data</param>
        /// <param name="length">length of data</param>
        public BufferEventArgs(byte[] buffer, int offset, int length)
        {
            Buffer = buffer;
            Offset = offset;
            Length = length;
        }
    }
}