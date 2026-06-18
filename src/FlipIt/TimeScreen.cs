using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenSaver
{
    internal abstract class TimeScreen
    {
        // A Control rather than a Form, so the same renderer can draw into the full-screen window
        // or into a small preview panel inside the settings dialog.
        protected Control _form;
        private Bitmap _buffer;
        private Graphics _bufferGraphics;
        private PrivateFontCollection _pfc = null;
        private FontFamily _fontFamily = null;

        protected abstract byte[] GetFontResource();

        // Subclasses render the frame here. They draw onto the off-screen buffer (via Gfx),
        // not directly to the form, so the screen never shows a half-drawn frame (no flicker).
        protected abstract void DrawCore();

        // The colour the frame is wiped with before drawing. The desktop clock overrides this for its
        // white / transparent background themes; everything else keeps the classic black.
        protected virtual Color ClearColor => Color.Black;

        internal void Draw()
        {
            // Render the whole frame to the off-screen buffer first...
            Gfx.Clear(ClearColor);
            DrawCore();
            // ...then present it to the form in a single blit. This is what removes the flicker
            // that was visible when each box was painted straight to the screen every second.
            using (var formGraphics = _form.CreateGraphics())
            {
                formGraphics.DrawImageUnscaled(_buffer, 0, 0);
            }
        }

        protected Graphics Gfx
        {
            get
            {
                if (_bufferGraphics == null)
                {
                    _buffer = new Bitmap(Math.Max(1, _form.ClientSize.Width), Math.Max(1, _form.ClientSize.Height));
                    _bufferGraphics = Graphics.FromImage(_buffer);
                    _bufferGraphics.TextRenderingHint = TextRenderingHint.AntiAlias;
                    _bufferGraphics.SmoothingMode = SmoothingMode.HighQuality;
                }
                return _bufferGraphics;
            }
        }

        protected FontFamily FontFamily
        {
            get
            {
                if (_fontFamily == null)
                {
                    if (_pfc == null)
                    {
                        _pfc = InitFontCollection();
                    }
                    _fontFamily = _pfc.Families[0];
                }
                return _fontFamily ?? (_fontFamily = _pfc.Families[0]);
            }
        }

        // Experimental
        protected int GetFontAscentPercent()
        {
            var ascent = FontFamily.GetCellAscent(FontStyle.Regular);
            var all =  FontFamily.GetEmHeight(FontStyle.Regular);
            return ascent * 100 / all;
        }
        
        private PrivateFontCollection InitFontCollection()
        {
            // We don't add both fonts at the same time because I can only get the private font collection
            // to return the first one we add. If the first one is the non-bold one and we ask for a bold one
            // then it seems to have an (inadequate) go at generating bold rather than using the one we gave it.
            // The system font collection does not seem to have this problem.
            // protected abstract PrivateFontCollection InitFontCollection();

            var pfc = new PrivateFontCollection();
            AddFont(pfc, GetFontResource());
            return pfc;
        }
        
        protected static readonly Color BackColorTop = Color.FromArgb(255, 18, 18, 18);
        protected static readonly Color BackColorBottom = Color.FromArgb(255, 10, 10, 10);
        protected static readonly Brush FontBrush = new SolidBrush(Color.FromArgb(255, 183, 183, 183));
        
        protected static void AddFont(PrivateFontCollection pfc, byte[] fontResource)
        {
            IntPtr ptr = Marshal.AllocCoTaskMem(fontResource.Length);  // create an unsafe memory block for the font data
            Marshal.Copy(fontResource, 0, ptr, fontResource.Length);  // copy the bytes to the unsafe memory block
            pfc.AddMemoryFont(ptr, fontResource.Length);    // pass the font to the font collection
            Marshal.FreeCoTaskMem(ptr);
        }

        protected string FormatAmPm(DateTime time)
        {
            // We format this ourselves because some cultures, such as nl-NL produce, results longer than 2 chars. ie. "a.m."
            // and we want to be consistent with the current time screen
            return time.Hour >= 12 ? "PM" : "AM";
        }

        internal virtual void DisposeResources()
        {
            if (_bufferGraphics != null)
            {
                _bufferGraphics.Dispose();
                _bufferGraphics = null;
            }
            if (_buffer != null)
            {
                _buffer.Dispose();
                _buffer = null;
            }
            if (_fontFamily != null)
            {
                _fontFamily.Dispose();
                _fontFamily = null;
            }
            if (_pfc != null)
            {
                _pfc.Dispose();
                _pfc = null;
            }
        }
    }
}