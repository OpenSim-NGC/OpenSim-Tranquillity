/*
 * Raw deflate, byte for byte as zlib 1.2.13 writes it with level 6, windowBits -15, memLevel 8 and the
 * default strategy: deflate_slow, longest_match, fill_window (deflate.c) and the block and Huffman code
 * (trees.c), for one whole input finished at once.
 *
 * Why it exists: Halcyon's iwStringCodec "gzip" compressed with .NET Framework's GZipStream, whose body is
 * stock zlib at level 6 (checked against Python's zlib 1.2.13 and Framework 4.8.1 on every golden vector).
 * .NET 10's DeflateStream uses zlib-ng, which writes different bytes.
 *
 * zlib (C) 1995-2022 Jean-loup Gailly and Mark Adler. zlib license: this software is provided 'as-is',
 * without any express or implied warranty. Permission is granted to anyone to use this software for any
 * purpose, including commercial applications, and to alter it and redistribute it freely, subject to:
 * 1. The origin of this software must not be misrepresented; 2. Altered source versions must be plainly
 * marked as such, and must not be misrepresented as being the original software; 3. This notice may not
 * be removed or altered from any source distribution. This file is an altered (ported) version.
 */

using System;
using System.Collections.Generic;

namespace Phlox.ScriptEngine.Codecs
{
    internal sealed class ZlibDeflate
    {
        // deflate.h / deflate.c constants for windowBits 15, memLevel 8, level 6.
        private const int MIN_MATCH = 3;
        private const int MAX_MATCH = 258;
        private const int MIN_LOOKAHEAD = MAX_MATCH + MIN_MATCH + 1;
        private const int W_BITS = 15;
        private const int W_SIZE = 1 << W_BITS;
        private const int W_MASK = W_SIZE - 1;
        private const int HASH_BITS = 8 + 7;
        private const int HASH_SIZE = 1 << HASH_BITS;
        private const int HASH_MASK = HASH_SIZE - 1;
        private const int HASH_SHIFT = (HASH_BITS + MIN_MATCH - 1) / MIN_MATCH;
        private const int WINDOW_SIZE = 2 * W_SIZE;
        private const int MAX_DIST = W_SIZE - MIN_LOOKAHEAD;
        private const int TOO_FAR = 4096;
        private const int WIN_INIT = MAX_MATCH;
        private const int LIT_BUFSIZE = 1 << (8 + 6);
        private const int SYM_END = (LIT_BUFSIZE - 1) * 3;
        private const int NIL = 0;
        // configuration_table[6] = {8, 16, 128, 128, deflate_slow}
        private const int GOOD_MATCH = 8;
        private const int MAX_LAZY = 16;
        private const int NICE_MATCH = 128;
        private const int MAX_CHAIN = 128;

        // trees.c constants
        private const int LENGTH_CODES = 29;
        private const int LITERALS = 256;
        private const int L_CODES = LITERALS + 1 + LENGTH_CODES;
        private const int D_CODES = 30;
        private const int BL_CODES = 19;
        private const int HEAP_SIZE = 2 * L_CODES + 1;
        private const int MAX_BITS = 15;
        private const int MAX_BL_BITS = 7;
        private const int END_BLOCK = 256;
        private const int REP_3_6 = 16;
        private const int REPZ_3_10 = 17;
        private const int REPZ_11_138 = 18;
        private const int STORED_BLOCK = 0;
        private const int STATIC_TREES = 1;
        private const int DYN_TREES = 2;

        private static readonly int[] extra_lbits = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        private static readonly int[] extra_dbits = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        private static readonly int[] extra_blbits = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 7 };
        private static readonly byte[] bl_order = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        // tr_static_init
        private static readonly ushort[] static_ltree_code = new ushort[L_CODES + 2];
        private static readonly ushort[] static_ltree_len = new ushort[L_CODES + 2];
        private static readonly ushort[] static_dtree_code = new ushort[D_CODES];
        private static readonly ushort[] static_dtree_len = new ushort[D_CODES];
        private static readonly byte[] _dist_code = new byte[512];
        private static readonly byte[] _length_code = new byte[MAX_MATCH - MIN_MATCH + 1];
        private static readonly int[] base_length = new int[LENGTH_CODES];
        private static readonly int[] base_dist = new int[D_CODES];

        static ZlibDeflate()
        {
            int n, code, length = 0, dist = 0;
            for (code = 0; code < LENGTH_CODES - 1; code++)
            {
                base_length[code] = length;
                for (n = 0; n < (1 << extra_lbits[code]); n++) _length_code[length++] = (byte)code;
            }
            _length_code[length - 1] = (byte)code;
            for (code = 0; code < 16; code++)
            {
                base_dist[code] = dist;
                for (n = 0; n < (1 << extra_dbits[code]); n++) _dist_code[dist++] = (byte)code;
            }
            dist >>= 7;
            for (; code < D_CODES; code++)
            {
                base_dist[code] = dist << 7;
                for (n = 0; n < (1 << (extra_dbits[code] - 7)); n++) _dist_code[256 + dist++] = (byte)code;
            }
            ushort[] bl_count = new ushort[MAX_BITS + 1];
            n = 0;
            while (n <= 143) { static_ltree_len[n++] = 8; bl_count[8]++; }
            while (n <= 255) { static_ltree_len[n++] = 9; bl_count[9]++; }
            while (n <= 279) { static_ltree_len[n++] = 7; bl_count[7]++; }
            while (n <= 287) { static_ltree_len[n++] = 8; bl_count[8]++; }
            GenCodes(static_ltree_code, static_ltree_len, L_CODES + 1, bl_count);
            for (n = 0; n < D_CODES; n++)
            {
                static_dtree_len[n] = 5;
                static_dtree_code[n] = (ushort)BiReverse(n, 5);
            }
        }

        /// <summary>Raw deflate of the whole input (deflate(Z_FINISH) after deflateInit2(6, Z_DEFLATED, -15, 8)).</summary>
        public static byte[] Compress(byte[] input)
        {
            ZlibDeflate s = new ZlibDeflate(input);
            s.DeflateSlow();
            return s.output.ToArray();
        }

        // ---- state (deflate_state) ----
        private readonly byte[] input;
        private int next_in;
        private readonly byte[] window = new byte[WINDOW_SIZE];
        private readonly ushort[] prev = new ushort[W_SIZE];
        private readonly ushort[] head = new ushort[HASH_SIZE];
        private int ins_h;
        private long block_start;
        private int match_length = MIN_MATCH - 1;
        private int prev_match;
        private bool match_available;
        private int strstart;
        private int match_start;
        private int lookahead;
        private int prev_length = MIN_MATCH - 1;
        private int insert;
        private long high_water;

        // ct_data is a union in zlib: Freq/Code share one field, Dad/Len the other.
        private readonly ushort[] dyn_ltree_fc = new ushort[HEAP_SIZE];
        private readonly ushort[] dyn_ltree_dl = new ushort[HEAP_SIZE];
        private readonly ushort[] dyn_dtree_fc = new ushort[2 * D_CODES + 1];
        private readonly ushort[] dyn_dtree_dl = new ushort[2 * D_CODES + 1];
        private readonly ushort[] bl_tree_fc = new ushort[2 * BL_CODES + 1];
        private readonly ushort[] bl_tree_dl = new ushort[2 * BL_CODES + 1];
        private int l_max_code, d_max_code, bl_max_code;
        private readonly ushort[] bl_count = new ushort[MAX_BITS + 1];
        private readonly int[] heap = new int[2 * L_CODES + 1];
        private int heap_len, heap_max;
        private readonly byte[] depth = new byte[2 * L_CODES + 1];
        private readonly byte[] sym_buf = new byte[LIT_BUFSIZE * 3];
        private int sym_next;
        private long opt_len, static_len;

        private readonly List<byte> output = new List<byte>();
        private ulong bi_buf;
        private int bi_valid;

        private ZlibDeflate(byte[] input)
        {
            this.input = input;
            InitBlock();
        }

        private int AvailIn { get { return input.Length - next_in; } }

        private int ReadBuf(int dest, int size)
        {
            int len = AvailIn;
            if (len > size) len = size;
            if (len == 0) return 0;
            Buffer.BlockCopy(input, next_in, window, dest, len);
            next_in += len;
            return len;
        }

        private void UpdateHash(int c) { ins_h = ((ins_h << HASH_SHIFT) ^ c) & HASH_MASK; }

        private int InsertString(int str)
        {
            UpdateHash(window[str + (MIN_MATCH - 1)]);
            int match_head = head[ins_h];
            prev[str & W_MASK] = (ushort)match_head;
            head[ins_h] = (ushort)str;
            return match_head;
        }

        private void SlideHash()
        {
            for (int n = 0; n < HASH_SIZE; n++) { int m = head[n]; head[n] = (ushort)(m >= W_SIZE ? m - W_SIZE : NIL); }
            for (int n = 0; n < W_SIZE; n++) { int m = prev[n]; prev[n] = (ushort)(m >= W_SIZE ? m - W_SIZE : NIL); }
        }

        private void FillWindow()
        {
            int n;
            int more;
            do
            {
                more = WINDOW_SIZE - lookahead - strstart;
                if (strstart >= W_SIZE + MAX_DIST)
                {
                    Buffer.BlockCopy(window, W_SIZE, window, 0, W_SIZE - more);
                    match_start -= W_SIZE;
                    strstart -= W_SIZE;
                    block_start -= W_SIZE;
                    if (insert > strstart) insert = strstart;
                    SlideHash();
                    more += W_SIZE;
                }
                if (AvailIn == 0) break;
                n = ReadBuf(strstart + lookahead, more);
                lookahead += n;
                if (lookahead + insert >= MIN_MATCH)
                {
                    int str = strstart - insert;
                    ins_h = window[str];
                    UpdateHash(window[str + 1]);
                    while (insert != 0)
                    {
                        UpdateHash(window[str + MIN_MATCH - 1]);
                        prev[str & W_MASK] = head[ins_h];
                        head[ins_h] = (ushort)str;
                        str++;
                        insert--;
                        if (lookahead + insert < MIN_MATCH) break;
                    }
                }
            } while (lookahead < MIN_LOOKAHEAD && AvailIn != 0);

            if (high_water < WINDOW_SIZE)
            {
                long curr = strstart + (long)lookahead;
                long init;
                if (high_water < curr)
                {
                    init = WINDOW_SIZE - curr;
                    if (init > WIN_INIT) init = WIN_INIT;
                    Array.Clear(window, (int)curr, (int)init);
                    high_water = curr + init;
                }
                else if (high_water < curr + WIN_INIT)
                {
                    init = curr + WIN_INIT - high_water;
                    if (init > WINDOW_SIZE - high_water) init = WINDOW_SIZE - high_water;
                    Array.Clear(window, (int)high_water, (int)init);
                    high_water += init;
                }
            }
        }

        private int LongestMatch(int cur_match)
        {
            int chain_length = MAX_CHAIN;
            int scan = strstart;
            int best_len = prev_length;
            int nice_match = NICE_MATCH;
            int limit = strstart > MAX_DIST ? strstart - MAX_DIST : NIL;
            int strend = strstart + MAX_MATCH;
            byte scan_end1 = window[scan + best_len - 1];
            byte scan_end = window[scan + best_len];

            if (prev_length >= GOOD_MATCH) chain_length >>= 2;
            if (nice_match > lookahead) nice_match = lookahead;

            do
            {
                int match = cur_match;
                if (window[match + best_len] != scan_end ||
                    window[match + best_len - 1] != scan_end1 ||
                    window[match] != window[scan] ||
                    window[match + 1] != window[scan + 1]) continue;

                // zlib skips scan[2]/match[2] (equal whenever the hashes are) and compares from offset 3.
                int k = 3;
                while (k < MAX_MATCH && window[scan + k] == window[match + k]) k++;
                int len = k;

                if (len > best_len)
                {
                    match_start = cur_match;
                    best_len = len;
                    if (len >= nice_match) break;
                    scan_end1 = window[scan + best_len - 1];
                    scan_end = window[scan + best_len];
                }
            } while ((cur_match = prev[cur_match & W_MASK]) > limit && --chain_length != 0);

            if (best_len <= lookahead) return best_len;
            return lookahead;
        }

        private void FlushBlockOnly(bool last)
        {
            if (block_start >= 0) TrFlushBlock((int)block_start, strstart - (int)block_start, last);
            else TrFlushBlock(-1, (int)(strstart - block_start), last);
            block_start = strstart;
        }

        private void DeflateSlow()
        {
            int hash_head;
            bool bflush;
            for (;;)
            {
                if (lookahead < MIN_LOOKAHEAD)
                {
                    FillWindow();
                    if (lookahead == 0) break;
                }
                hash_head = NIL;
                if (lookahead >= MIN_MATCH) hash_head = InsertString(strstart);

                prev_length = match_length;
                prev_match = match_start;
                match_length = MIN_MATCH - 1;

                if (hash_head != NIL && prev_length < MAX_LAZY && strstart - hash_head <= MAX_DIST)
                {
                    match_length = LongestMatch(hash_head);
                    if (match_length <= 5 && (match_length == MIN_MATCH && strstart - match_start > TOO_FAR))
                        match_length = MIN_MATCH - 1;
                }

                if (prev_length >= MIN_MATCH && match_length <= prev_length)
                {
                    int max_insert = strstart + lookahead - MIN_MATCH;
                    bflush = TrTally(strstart - 1 - prev_match, prev_length - MIN_MATCH);
                    lookahead -= prev_length - 1;
                    prev_length -= 2;
                    do
                    {
                        if (++strstart <= max_insert) InsertString(strstart);
                    } while (--prev_length != 0);
                    match_available = false;
                    match_length = MIN_MATCH - 1;
                    strstart++;
                    if (bflush) FlushBlockOnly(false);
                }
                else if (match_available)
                {
                    bflush = TrTally(0, window[strstart - 1]);
                    if (bflush) FlushBlockOnly(false);
                    strstart++;
                    lookahead--;
                }
                else
                {
                    match_available = true;
                    strstart++;
                    lookahead--;
                }
            }
            if (match_available)
            {
                TrTally(0, window[strstart - 1]);
                match_available = false;
            }
            insert = strstart < MIN_MATCH - 1 ? strstart : MIN_MATCH - 1;
            FlushBlockOnly(true);
        }

        // ---------------- trees.c ----------------

        private void InitBlock()
        {
            for (int n = 0; n < L_CODES; n++) dyn_ltree_fc[n] = 0;
            for (int n = 0; n < D_CODES; n++) dyn_dtree_fc[n] = 0;
            for (int n = 0; n < BL_CODES; n++) bl_tree_fc[n] = 0;
            dyn_ltree_fc[END_BLOCK] = 1;
            opt_len = static_len = 0;
            sym_next = 0;
        }

        private static int DCode(int dist) { return dist < 256 ? _dist_code[dist] : _dist_code[256 + (dist >> 7)]; }

        private bool TrTally(int dist, int lc)
        {
            sym_buf[sym_next++] = (byte)dist;
            sym_buf[sym_next++] = (byte)(dist >> 8);
            sym_buf[sym_next++] = (byte)lc;
            if (dist == 0)
            {
                dyn_ltree_fc[lc]++;
            }
            else
            {
                dist--;
                dyn_ltree_fc[_length_code[lc] + LITERALS + 1]++;
                dyn_dtree_fc[DCode(dist)]++;
            }
            return sym_next == SYM_END;
        }

        private void SendBits(int value, int length)
        {
            bi_buf |= (ulong)(uint)value << bi_valid;
            bi_valid += length;
            while (bi_valid >= 8)
            {
                output.Add((byte)bi_buf);
                bi_buf >>= 8;
                bi_valid -= 8;
            }
        }

        private void BiWindup()
        {
            if (bi_valid > 0) output.Add((byte)bi_buf);
            bi_buf = 0;
            bi_valid = 0;
        }

        private static int BiReverse(int code, int len)
        {
            int res = 0;
            do
            {
                res |= code & 1;
                code >>= 1;
                res <<= 1;
            } while (--len > 0);
            return res >> 1;
        }

        private static void GenCodes(ushort[] code, ushort[] len, int max_code, ushort[] bl_count)
        {
            ushort[] next_code = new ushort[MAX_BITS + 1];
            int c = 0;
            for (int bits = 1; bits <= MAX_BITS; bits++)
            {
                c = (c + bl_count[bits - 1]) << 1;
                next_code[bits] = (ushort)c;
            }
            for (int n = 0; n <= max_code; n++)
            {
                int l = len[n];
                if (l == 0) continue;
                code[n] = (ushort)BiReverse(next_code[l]++, l);
            }
        }

        private bool Smaller(ushort[] fc, int n, int m)
        {
            return fc[n] < fc[m] || (fc[n] == fc[m] && depth[n] <= depth[m]);
        }

        private void PqDownHeap(ushort[] fc, int k)
        {
            int v = heap[k];
            int j = k << 1;
            while (j <= heap_len)
            {
                if (j < heap_len && Smaller(fc, heap[j + 1], heap[j])) j++;
                if (Smaller(fc, v, heap[j])) break;
                heap[k] = heap[j];
                k = j;
                j <<= 1;
            }
            heap[k] = v;
        }

        // tree 0 = literal/length, 1 = distance, 2 = bit length
        private void GenBitlen(ushort[] fc, ushort[] dl, int max_code, ushort[] stree_len, int[] extra, int extra_base, int max_length)
        {
            int h, n, m, bits, xbits;
            int f;
            int overflow = 0;
            for (bits = 0; bits <= MAX_BITS; bits++) bl_count[bits] = 0;

            dl[heap[heap_max]] = 0;
            for (h = heap_max + 1; h < HEAP_SIZE; h++)
            {
                n = heap[h];
                bits = dl[dl[n]] + 1;
                if (bits > max_length) { bits = max_length; overflow++; }
                dl[n] = (ushort)bits;
                if (n > max_code) continue;
                bl_count[bits]++;
                xbits = 0;
                if (n >= extra_base) xbits = extra[n - extra_base];
                f = fc[n];
                opt_len += (long)f * (bits + xbits);
                if (stree_len != null) static_len += (long)f * (stree_len[n] + xbits);
            }
            if (overflow == 0) return;

            do
            {
                bits = max_length - 1;
                while (bl_count[bits] == 0) bits--;
                bl_count[bits]--;
                bl_count[bits + 1] += 2;
                bl_count[max_length]--;
                overflow -= 2;
            } while (overflow > 0);

            for (bits = max_length; bits != 0; bits--)
            {
                n = bl_count[bits];
                while (n != 0)
                {
                    m = heap[--h];
                    if (m > max_code) continue;
                    if (dl[m] != bits)
                    {
                        opt_len += ((long)bits - dl[m]) * fc[m];
                        dl[m] = (ushort)bits;
                    }
                    n--;
                }
            }
        }

        private int BuildTree(ushort[] fc, ushort[] dl, ushort[] stree_len, int[] extra, int extra_base, int elems, int max_length)
        {
            int n, m;
            int max_code = -1;
            int node;

            heap_len = 0;
            heap_max = HEAP_SIZE;
            for (n = 0; n < elems; n++)
            {
                if (fc[n] != 0)
                {
                    heap[++heap_len] = max_code = n;
                    depth[n] = 0;
                }
                else
                {
                    dl[n] = 0;
                }
            }
            while (heap_len < 2)
            {
                node = heap[++heap_len] = (max_code < 2 ? ++max_code : 0);
                fc[node] = 1;
                depth[node] = 0;
                opt_len--;
                if (stree_len != null) static_len -= stree_len[node];
            }
            for (n = heap_len / 2; n >= 1; n--) PqDownHeap(fc, n);

            node = elems;
            do
            {
                n = heap[1];
                heap[1] = heap[heap_len--];
                PqDownHeap(fc, 1);
                m = heap[1];
                heap[--heap_max] = n;
                heap[--heap_max] = m;
                fc[node] = (ushort)(fc[n] + fc[m]);
                depth[node] = (byte)((depth[n] >= depth[m] ? depth[n] : depth[m]) + 1);
                dl[n] = dl[m] = (ushort)node;
                heap[1] = node++;
                PqDownHeap(fc, 1);
            } while (heap_len >= 2);
            heap[--heap_max] = heap[1];

            GenBitlen(fc, dl, max_code, stree_len, extra, extra_base, max_length);
            GenCodes(fc, dl, max_code, bl_count);
            return max_code;
        }

        private void ScanTree(ushort[] dl, int max_code)
        {
            int n;
            int prevlen = -1;
            int curlen;
            int nextlen = dl[0];
            int count = 0;
            int max_count = 7;
            int min_count = 4;
            if (nextlen == 0) { max_count = 138; min_count = 3; }
            dl[max_code + 1] = 0xffff;
            for (n = 0; n <= max_code; n++)
            {
                curlen = nextlen;
                nextlen = dl[n + 1];
                if (++count < max_count && curlen == nextlen) continue;
                else if (count < min_count) bl_tree_fc[curlen] = (ushort)(bl_tree_fc[curlen] + count);
                else if (curlen != 0)
                {
                    if (curlen != prevlen) bl_tree_fc[curlen]++;
                    bl_tree_fc[REP_3_6]++;
                }
                else if (count <= 10) bl_tree_fc[REPZ_3_10]++;
                else bl_tree_fc[REPZ_11_138]++;
                count = 0;
                prevlen = curlen;
                if (nextlen == 0) { max_count = 138; min_count = 3; }
                else if (curlen == nextlen) { max_count = 6; min_count = 3; }
                else { max_count = 7; min_count = 4; }
            }
        }

        private void SendCode(int c, ushort[] code, ushort[] len) { SendBits(code[c], len[c]); }

        private void SendTree(ushort[] dl, int max_code)
        {
            int n;
            int prevlen = -1;
            int curlen;
            int nextlen = dl[0];
            int count = 0;
            int max_count = 7;
            int min_count = 4;
            if (nextlen == 0) { max_count = 138; min_count = 3; }
            for (n = 0; n <= max_code; n++)
            {
                curlen = nextlen;
                nextlen = dl[n + 1];
                if (++count < max_count && curlen == nextlen) continue;
                else if (count < min_count)
                {
                    do { SendCode(curlen, bl_tree_fc, bl_tree_dl); } while (--count != 0);
                }
                else if (curlen != 0)
                {
                    if (curlen != prevlen) { SendCode(curlen, bl_tree_fc, bl_tree_dl); count--; }
                    SendCode(REP_3_6, bl_tree_fc, bl_tree_dl);
                    SendBits(count - 3, 2);
                }
                else if (count <= 10)
                {
                    SendCode(REPZ_3_10, bl_tree_fc, bl_tree_dl);
                    SendBits(count - 3, 3);
                }
                else
                {
                    SendCode(REPZ_11_138, bl_tree_fc, bl_tree_dl);
                    SendBits(count - 11, 7);
                }
                count = 0;
                prevlen = curlen;
                if (nextlen == 0) { max_count = 138; min_count = 3; }
                else if (curlen == nextlen) { max_count = 6; min_count = 3; }
                else { max_count = 7; min_count = 4; }
            }
        }

        private int BuildBlTree()
        {
            int max_blindex;
            ScanTree(dyn_ltree_dl, l_max_code);
            ScanTree(dyn_dtree_dl, d_max_code);
            bl_max_code = BuildTree(bl_tree_fc, bl_tree_dl, null, extra_blbits, 0, BL_CODES, MAX_BL_BITS);
            for (max_blindex = BL_CODES - 1; max_blindex >= 3; max_blindex--)
            {
                if (bl_tree_dl[bl_order[max_blindex]] != 0) break;
            }
            opt_len += 3 * ((long)max_blindex + 1) + 5 + 5 + 4;
            return max_blindex;
        }

        private void SendAllTrees(int lcodes, int dcodes, int blcodes)
        {
            SendBits(lcodes - 257, 5);
            SendBits(dcodes - 1, 5);
            SendBits(blcodes - 4, 4);
            for (int rank = 0; rank < blcodes; rank++) SendBits(bl_tree_dl[bl_order[rank]], 3);
            SendTree(dyn_ltree_dl, lcodes - 1);
            SendTree(dyn_dtree_dl, dcodes - 1);
        }

        private void CompressBlock(ushort[] lcode, ushort[] llen, ushort[] dcode, ushort[] dlen)
        {
            int sx = 0;
            if (sym_next != 0)
            {
                do
                {
                    int dist = sym_buf[sx++];
                    dist += sym_buf[sx++] << 8;
                    int lc = sym_buf[sx++];
                    if (dist == 0)
                    {
                        SendCode(lc, lcode, llen);
                    }
                    else
                    {
                        int code = _length_code[lc];
                        SendCode(code + LITERALS + 1, lcode, llen);
                        int extra = extra_lbits[code];
                        if (extra != 0)
                        {
                            lc -= base_length[code];
                            SendBits(lc, extra);
                        }
                        dist--;
                        code = DCode(dist);
                        SendCode(code, dcode, dlen);
                        extra = extra_dbits[code];
                        if (extra != 0)
                        {
                            dist -= base_dist[code];
                            SendBits(dist, extra);
                        }
                    }
                } while (sx < sym_next);
            }
            SendCode(END_BLOCK, lcode, llen);
        }

        private void TrStoredBlock(int buf, int stored_len, bool last)
        {
            SendBits((STORED_BLOCK << 1) + (last ? 1 : 0), 3);
            BiWindup();
            output.Add((byte)stored_len);
            output.Add((byte)(stored_len >> 8));
            output.Add((byte)~stored_len);
            output.Add((byte)(~stored_len >> 8));
            for (int i = 0; i < stored_len; i++) output.Add(window[buf + i]);
        }

        // buf < 0: zlib's NULL buffer (the block started before the window slid past it).
        private void TrFlushBlock(int buf, int stored_len, bool last)
        {
            long opt_lenb, static_lenb;
            int max_blindex;

            l_max_code = BuildTree(dyn_ltree_fc, dyn_ltree_dl, static_ltree_len, extra_lbits, LITERALS + 1, L_CODES, MAX_BITS);
            d_max_code = BuildTree(dyn_dtree_fc, dyn_dtree_dl, static_dtree_len, extra_dbits, 0, D_CODES, MAX_BITS);
            max_blindex = BuildBlTree();

            opt_lenb = (opt_len + 3 + 7) >> 3;
            static_lenb = (static_len + 3 + 7) >> 3;
            if (static_lenb <= opt_lenb) opt_lenb = static_lenb;

            if (stored_len + 4 <= opt_lenb && buf >= 0)
            {
                TrStoredBlock(buf, stored_len, last);
            }
            else if (static_lenb == opt_lenb)
            {
                SendBits((STATIC_TREES << 1) + (last ? 1 : 0), 3);
                CompressBlock(static_ltree_code, static_ltree_len, static_dtree_code, static_dtree_len);
            }
            else
            {
                SendBits((DYN_TREES << 1) + (last ? 1 : 0), 3);
                SendAllTrees(l_max_code + 1, d_max_code + 1, max_blindex + 1);
                CompressBlock(dyn_ltree_fc, dyn_ltree_dl, dyn_dtree_fc, dyn_dtree_dl);
            }
            InitBlock();
            if (last) BiWindup();
        }
    }
}
