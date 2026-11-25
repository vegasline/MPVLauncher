// MPEG-TS helpers.
//
// A few sites serve TS segments under a fake image extension (file000.jpeg).
// The bytes are still 188-byte packets, so the only thing worth doing is
// making sure nothing in front of the first packet leaks into the demuxer.
const PACKET = 188;
const SYNC = 0x47;

export function startsWithTsSync(buffer) {
  if (!buffer || buffer.length < PACKET * 3) return false;
  return buffer[0] === SYNC && buffer[PACKET] === SYNC && buffer[PACKET * 2] === SYNC;
}

/**
 * Returns the buffer starting at the first position where three consecutive
 * 188-byte packets line up, or the original buffer when no such position
 * exists. Only call this for bodies that did not already start on a sync byte.
 */
export function stripToTsSync(buffer) {
  if (!buffer) return buffer;
  const limit = Math.min(buffer.length - PACKET * 3, 4096);
  for (let i = 1; i <= limit; i++) {
    if (buffer[i] === SYNC && buffer[i + PACKET] === SYNC && buffer[i + PACKET * 2] === SYNC) {
      return buffer.subarray(i);
    }
  }
  return buffer;
}
