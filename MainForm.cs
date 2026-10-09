using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace CoinCounter
{
    public class MainForm : Form
    {
        // ---- Diameter thresholds in pixels (calibrated for the 776x1024 scan) ----
        // Real coin sizes: 5c=15mm, 10c=17mm, 25c=20mm, P1=24mm, P5=27mm
        const double T_5C_10C = 61.0;   // below -> 5 sentimo
        const double T_10C_25C = 69.5;  // below -> 10 sentimo
        const double T_25C_1P = 82.0;   // below -> 25 sentimo
        const double T_1P_5P = 94.5;    // below -> 1 peso, else 5 peso
        const int MIN_AREA = 500;       // ignore specks / noise

        PictureBox pic = new PictureBox();
        TextBox txtResult = new TextBox();
        Button btnOpen = new Button();
        Button btnProcess = new Button();
        Bitmap original;

        public MainForm()
        {
            Text = "Philippine Coin Counter - DIP Activity";
            Width = 1200; Height = 850;

            btnOpen.Text = "Open Image";
            btnOpen.SetBounds(10, 10, 110, 32);
            btnOpen.Click += BtnOpen_Click;

            btnProcess.Text = "Count Coins";
            btnProcess.SetBounds(130, 10, 110, 32);
            btnProcess.Click += BtnProcess_Click;

            pic.SetBounds(10, 50, 700, 750);
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.BorderStyle = BorderStyle.FixedSingle;

            txtResult.SetBounds(720, 50, 440, 750);
            txtResult.Multiline = true;
            txtResult.ReadOnly = true;
            txtResult.Font = new Font("Consolas", 12);
            txtResult.ScrollBars = ScrollBars.Vertical;

            Controls.AddRange(new Control[] { btnOpen, btnProcess, pic, txtResult });

            Load += MainForm_Load;
        }

        // Auto-load GetImage.jpeg if it sits next to the .exe
        void MainForm_Load(object sender, EventArgs e)
        {
            string path = Path.Combine(Application.StartupPath, "GetImage.jpeg");
            if (File.Exists(path))
            {
                LoadImage(path);
                txtResult.Text = "Sample image loaded. Click 'Count Coins'.";
            }
            else
            {
                txtResult.Text = "Click 'Open Image' to choose a coin image.";
            }
        }

        void LoadImage(string path)
        {
            // Copy so the file is not locked
            using (Bitmap tmp = new Bitmap(path))
                original = new Bitmap(tmp);
            pic.Image = original;
        }

        void BtnOpen_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp";
                if (ofd.ShowDialog() != DialogResult.OK) return;
                LoadImage(ofd.FileName);
                txtResult.Text = "Image loaded. Click 'Count Coins'.";
            }
        }

        void BtnProcess_Click(object sender, EventArgs e)
        {
            if (original == null) { MessageBox.Show("Open an image first."); return; }

            int w = original.Width, h = original.Height;

            // 1. Grayscale
            byte[] gray = ToGrayscale(original);

            // 2. Otsu threshold -> coins (dark) = true
            int t = OtsuThreshold(gray);
            bool[] fg = new bool[w * h];
            for (int i = 0; i < fg.Length; i++) fg[i] = gray[i] < t;

            // 3. Fill holes so each coin is a solid disk
            bool[] filled = FillHoles(fg, w, h);

            // 4. Connected component labeling + measurement
            List<Blob> blobs = LabelBlobs(filled, w, h);

            // 5. Classify by equivalent diameter
            int c5 = 0, c10 = 0, c25 = 0, p1 = 0, p5 = 0;
            Bitmap output = new Bitmap(original);
            using (Graphics g = Graphics.FromImage(output))
            using (Font f = new Font("Arial", 14, FontStyle.Bold))
            {
                foreach (Blob b in blobs)
                {
                    if (b.Area < MIN_AREA) continue;
                    double d = 2.0 * Math.Sqrt(b.Area / Math.PI);
                    string label; Color col;

                    if (d < T_5C_10C)       { c5++;  label = "5c";  col = Color.Red; }
                    else if (d < T_10C_25C) { c10++; label = "10c"; col = Color.Orange; }
                    else if (d < T_25C_1P)  { c25++; label = "25c"; col = Color.Blue; }
                    else if (d < T_1P_5P)   { p1++;  label = "P1";  col = Color.Green; }
                    else                    { p5++;  label = "P5";  col = Color.Magenta; }

                    float r = (float)(d / 2.0);
                    using (Pen pen = new Pen(col, 3))
                        g.DrawEllipse(pen, (float)b.CX - r, (float)b.CY - r, 2 * r, 2 * r);
                    using (Brush br = new SolidBrush(col))
                        g.DrawString(label, f, br, (float)b.CX - 18, (float)b.CY - 12);
                }
            }
            pic.Image = output;

            // 6. Report
            decimal total = c5 * 0.05m + c10 * 0.10m + c25 * 0.25m + p1 * 1m + p5 * 5m;
            int count = c5 + c10 + c25 + p1 + p5;
            string nl = Environment.NewLine;
            txtResult.Text =
                "COIN COUNT RESULTS" + nl +
                "==================================" + nl +
                string.Format("5 sentimo  x {0,3} = P{1,8:F2}", c5, c5 * 0.05m) + nl +
                string.Format("10 sentimo x {0,3} = P{1,8:F2}", c10, c10 * 0.10m) + nl +
                string.Format("25 sentimo x {0,3} = P{1,8:F2}", c25, c25 * 0.25m) + nl +
                string.Format("1 peso     x {0,3} = P{1,8:F2}", p1, p1 * 1m) + nl +
                string.Format("5 peso     x {0,3} = P{1,8:F2}", p5, p5 * 5m) + nl +
                "----------------------------------" + nl +
                "Total coins : " + count + nl +
                "TOTAL VALUE : P" + total.ToString("F2");
        }

        // ---------------- Image processing routines (hand-written) ----------------

        static byte[] ToGrayscale(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            byte[] gray = new byte[w * h];
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte[] row = new byte[Math.Abs(data.Stride)];
                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        IntPtr.Add(data.Scan0, y * data.Stride), row, 0, w * 3);
                    for (int x = 0; x < w; x++)
                    {
                        int b = row[x * 3], g = row[x * 3 + 1], r = row[x * 3 + 2];
                        gray[y * w + x] = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            return gray;
        }

        static int OtsuThreshold(byte[] gray)
        {
            int[] hist = new int[256];
            foreach (byte v in gray) hist[v]++;
            long total = gray.Length, sumAll = 0;
            for (int i = 0; i < 256; i++) sumAll += (long)i * hist[i];

            long wB = 0, sumB = 0; double best = -1; int thr = 128;
            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                long wF = total - wB;
                if (wF == 0) break;
                sumB += (long)t * hist[t];
                double mB = (double)sumB / wB;
                double mF = (double)(sumAll - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > best) { best = between; thr = t; }
            }
            return thr;
        }

        // Flood-fill the background from the image border; anything not reached
        // is part of a coin (including holes inside it).
        static bool[] FillHoles(bool[] fg, int w, int h)
        {
            bool[] bgReached = new bool[w * h];
            Queue<int> q = new Queue<int>();

            Action<int, int> seed = (x, y) =>
            {
                int i = y * w + x;
                if (!fg[i] && !bgReached[i]) { bgReached[i] = true; q.Enqueue(i); }
            };
            for (int x = 0; x < w; x++) { seed(x, 0); seed(x, h - 1); }
            for (int y = 0; y < h; y++) { seed(0, y); seed(w - 1, y); }

            int[] dx = { 1, -1, 0, 0 }, dy = { 0, 0, 1, -1 };
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                int x = i % w, y = i / w;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + dx[k], ny = y + dy[k];
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (!fg[ni] && !bgReached[ni]) { bgReached[ni] = true; q.Enqueue(ni); }
                }
            }

            bool[] filled = new bool[w * h];
            for (int i = 0; i < filled.Length; i++) filled[i] = !bgReached[i];
            return filled;
        }

        class Blob { public int Area; public double CX, CY; }

        // 4-connected component labeling (BFS), computes area and centroid
        static List<Blob> LabelBlobs(bool[] mask, int w, int h)
        {
            bool[] visited = new bool[w * h];
            List<Blob> blobs = new List<Blob>();
            Queue<int> q = new Queue<int>();
            int[] dx = { 1, -1, 0, 0 }, dy = { 0, 0, 1, -1 };

            for (int s = 0; s < mask.Length; s++)
            {
                if (!mask[s] || visited[s]) continue;
                Blob b = new Blob();
                long sx = 0, sy = 0;
                visited[s] = true; q.Enqueue(s);
                while (q.Count > 0)
                {
                    int i = q.Dequeue();
                    int x = i % w, y = i / w;
                    b.Area++; sx += x; sy += y;
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = x + dx[k], ny = y + dy[k];
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int ni = ny * w + nx;
                        if (mask[ni] && !visited[ni]) { visited[ni] = true; q.Enqueue(ni); }
                    }
                }
                b.CX = (double)sx / b.Area;
                b.CY = (double)sy / b.Area;
                blobs.Add(b);
            }
            return blobs;
        }
    }
}
