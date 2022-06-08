using System;
using System.Globalization;
using System.Diagnostics;

namespace FamiStudio
{
    public class Grid : Control
    {
        public delegate void ValueChangedDelegate(Control sender, int rowIndex, int colIndex, object value);
        public delegate void ButtonPressedDelegate(Control sender, int rowIndex, int colIndex);
        public delegate void CellClickedDelegate(Control sender, bool left, int rowIndex, int colIndex);
        public delegate void CellDoubleClickedDelegate(Control sender, int rowIndex, int colIndex);
        public delegate void HeaderCellClickedDelegate(Control sender, int colIndex);

        public event ValueChangedDelegate ValueChanged;
        public event ButtonPressedDelegate ButtonPressed;
        public event CellClickedDelegate CellClicked;
        public event CellDoubleClickedDelegate CellDoubleClicked;
        public event HeaderCellClickedDelegate HeaderCellClicked;

        private int scroll;
        private int maxScroll;
        private int hoverRow = -1;
        private int hoverCol = -1;
        private bool hoverButton;
        private int dropDownRow = -1;
        private int dropDownCol = -1;
        private int numRows;
        private int numItemRows;
        private int numHeaderRows;
        private object[,] data;
        private int[] columnWidths;
        private int[] columnOffsets;
        private bool[] columnEnabled;
        private bool hasAnyDropDowns;
        private bool fullRowSelect;
        private ColumnDesc[] columns;

        private bool draggingScrollbars;
        private bool draggingSlider;
        private int  captureScrollBarPos;
        private int  captureMouseY;
        private int  sliderCol;
        private int  sliderRow;

        private DropDown dropDownInactive;
        private DropDown dropDownActive;

        private BitmapAtlasRef bmpCheckOn;
        private BitmapAtlasRef bmpCheckOff;

        private int margin         = DpiScaling.ScaleForWindow(4);
        private int scrollBarWidth = DpiScaling.ScaleForWindow(10);
        private int rowHeight      = DpiScaling.ScaleForWindow(20);
        private int checkBoxWidth  = DpiScaling.ScaleForWindow(20);

        public int ItemCount => data.GetLength(0);
        public bool FullRowSelect { get => fullRowSelect; set => fullRowSelect = value; }

        public Grid(Dialog dlg, ColumnDesc[] columnDescs, int rows, bool hasHeader = true) : base(dlg) 
        {
            columns = columnDescs;
            numRows = rows;
            numItemRows = hasHeader ? numRows - 1 : numRows;
            numHeaderRows = hasHeader ? 1 : 0;
            height = numRows * rowHeight;

            foreach (var col in columnDescs)
            {
                if (col.Type == ColumnType.DropDown)
                {
                    hasAnyDropDowns = true;
                    break;
                }
            }

            if (hasAnyDropDowns)
            {
                dropDownInactive = new DropDown(dlg, new[] { "" }, 0, true);
                dropDownActive = new DropDown(dlg, new[] { "" }, 0);
                dropDownInactive.SetRowHeight(rowHeight);
                dropDownActive.SetRowHeight(rowHeight);
                dropDownInactive.Visible = false;
                dropDownActive.Visible = false;
                dropDownActive.ListClosing += DropDownActive_ListClosing;
                dropDownActive.SelectedIndexChanged += DropDownActive_SelectedIndexChanged;
            }

            columnEnabled = new bool[columns.Length];
            for (int i = 0; i < columnEnabled.Length; i++)
                columnEnabled[i] = true;
        }

        public void SetColumnEnabled(int col, bool enabled)
        {
            // This is the only one that I added support to disabled state right now.
            //Debug.Assert(columns[col].Type == ColumnType.Slider);

            columnEnabled[col] = enabled;
            MarkDirty();
        }

        private void DropDownActive_SelectedIndexChanged(Control sender, int index)
        {
            if (dropDownRow >= 0 && dropDownCol >= 0 && dropDownActive.Visible)
            {
                data[dropDownRow, dropDownCol] = dropDownActive.Text;
                ValueChanged?.Invoke(this, dropDownRow, dropDownCol, dropDownActive.Text);
                MarkDirty();
            }
        }

        private void DropDownActive_ListClosing(Control sender)
        {
            Debug.Assert(dropDownActive.Visible);
            dropDownActive.Visible = false;
            dropDownRow = -1;
            dropDownCol = -1;
            MarkDirty();
        }

        public void UpdateData(int row, int col, object val)
        {
            data[row, col] = val;
            MarkDirty();
        }

        public void UpdateData(object[,] newData)
        {
            data = newData;
            Debug.Assert(data.GetLength(1) == columns.Length);

            if (parentDialog != null)
                UpdateLayout();
            MarkDirty();
        }

        public void RenameColumns(string[] columnNames)
        {
            Debug.Assert(columnNames.Length == columns.Length);
            for (int i = 0; i < columnNames.Length; i++)
                columns[i].Name = columnNames[i];
            MarkDirty();
        }

        public object GetData(int row, int col)
        {
            return data[row, col];
        }

        private void UpdateLayout()
        {
            var actualScrollBarWidth = data != null && data.GetLength(0) > numItemRows ? scrollBarWidth : 0;
            var actualWidth = width - actualScrollBarWidth;
            var totalWidth = 0;

            columnWidths = new int[columns.Length];
            columnOffsets = new int[columns.Length + 1];

            for (int i = 0; i < columns.Length - 1; i++)
            {
                var col = columns[i];
                var colWidth = col.Type == ColumnType.CheckBox || col.Type == ColumnType.Image ? checkBoxWidth : (int)Math.Round(col.Width * actualWidth);

                columnWidths[i] = colWidth;
                columnOffsets[i] = totalWidth;
                totalWidth += colWidth;
            }

            columnWidths[columns.Length - 1] = actualWidth - totalWidth;
            columnOffsets[columns.Length - 1] = totalWidth;
            columnOffsets[columns.Length] = width - 1;

            maxScroll = data != null ? Math.Max(0, data.GetLength(0) - numItemRows) : 0;
            scroll = Utils.Clamp(scroll, 0, maxScroll);
        }

        public void ResetScroll()
        {
            SetAndMarkDirty(ref scroll, 0);
        }

        protected override void OnRenderInitialized(Graphics g)
        {
            bmpCheckOn  = g.GetBitmapAtlasRef("CheckBoxYes");
            bmpCheckOff = g.GetBitmapAtlasRef("CheckBoxNo");
        }

        protected override void OnAddedToDialog()
        {
            UpdateLayout();

            if (hasAnyDropDowns)
            {
                parentDialog.AddControl(dropDownInactive);
                parentDialog.AddControl(dropDownActive);
            }
        }

        private bool PixelToCell(int x, int y, out int row, out int col)
        {
            row = -1;
            col = -1;

            var maxX = width - (GetScrollBarParams(out _, out _) ? scrollBarWidth : 0);

            if (x < 0 || x > maxX || y > height)
                return false;

            for (int i = 1; i < columnOffsets.Length; i++)
            {
                if (x <= columnOffsets[i])
                {
                    col = i - 1;
                    break;
                }
            }

            if (!columnEnabled[col])
            {
                col = -1;
                return false;
            }

            // Row -1 will mean header.
            row = y / rowHeight - numHeaderRows + scroll;

            Debug.Assert(col >= 0);
            return row >= 0 && row < data.GetLength(0);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var valid = PixelToCell(e.X, e.Y, out var row, out var col);

            if (e.Left)
            {
                MarkDirty();

                if (e.Y > rowHeight * numHeaderRows)
                {
                    if (GetScrollBarParams(out var scrollBarPos, out var scrollBarSize) && e.X > width - scrollBarWidth)
                    {
                        var y = e.Y - rowHeight * numHeaderRows;

                        if (y < scrollBarPos)
                        {
                            scroll = Math.Max(0, scroll - 3);
                        }
                        else if (y > (scrollBarPos + scrollBarSize))
                        {
                            scroll = Math.Min(maxScroll, scroll + 3);
                        }
                        else
                        {
                            Capture = true;
                            draggingScrollbars = true;
                            captureScrollBarPos = scrollBarPos;
                            captureMouseY = e.Y;
                        }

                        return;
                    }
                    else
                    {
                        if (valid)
                        { 
                            var colDesc = columns[col];
                            var colEnabled = columnEnabled[col];

                            switch (colDesc.Type)
                            {
                                case ColumnType.Button:
                                {
                                    if (IsPointInButton(e.X, row, col))
                                        ButtonPressed?.Invoke(this, row, col);
                                    break;
                                }
                                case ColumnType.CheckBox:
                                {
                                    data[row, col] = !(bool)data[row, col];
                                    ValueChanged?.Invoke(this, row, col, data[row, col]);
                                    break;
                                }
                                case ColumnType.Slider:
                                {
                                    Capture = true;
                                    draggingSlider = true;
                                    sliderCol = col;
                                    sliderRow = row;
                                    data[row, col] = (int)Math.Round(Utils.Lerp(0, 100, Utils.Saturate((e.X - columnOffsets[col]) / (float)columnWidths[col])));
                                    ValueChanged?.Invoke(this, row, col, data[row, col]);
                                    break;
                                }
                                case ColumnType.DropDown:
                                {
                                    dropDownActive.Visible = true;
                                    dropDownActive.Move(left + columnOffsets[col], top + (row + numHeaderRows - scroll) * rowHeight, columnWidths[col], rowHeight);
                                    dropDownActive.SetItems(colDesc.DropDownValues);
                                    dropDownActive.SelectedIndex = Array.IndexOf(colDesc.DropDownValues, (string)data[row, col]);
                                    dropDownActive.SetListOpened(true);
                                    dropDownActive.GrabDialogFocus();
                                    dropDownRow = row;
                                    dropDownCol = col;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            if (valid)
            {
                if (e.Left || e.Right)
                    CellClicked?.Invoke(this, e.Left, row, col);
            }
            else if (e.Left && row < 0 && col >= 0)
            {
                HeaderCellClicked?.Invoke(this, col);
            }
        }

        private bool IsPointInButton(int x, int row, int col)
        {
            if (row < 0 || col < 0 || !columnEnabled[col])
                return false;
            var cellX = x - columnOffsets[col];
            var buttonX = cellX - columnWidths[col] + rowHeight;
            return buttonX >= 0 && buttonX < rowHeight;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (draggingSlider)
            {
                var newSliderVal = (int)Math.Round(Utils.Lerp(0, 100, Utils.Saturate((e.X - columnOffsets[sliderCol]) / (float)columnWidths[sliderCol])));
                if (newSliderVal != (int)data[sliderRow, sliderCol])
                {
                    data[sliderRow, sliderCol] = newSliderVal;
                    ValueChanged?.Invoke(this, sliderRow, sliderCol, newSliderVal);
                    MarkDirty();
                }
            }
            else if (draggingScrollbars)
            {
                GetScrollBarParams(out var scrollBarPos, out var scrollBarSize);
                var newScrollBarPos = captureScrollBarPos + (e.Y - captureMouseY);
                var ratio = newScrollBarPos / (float)(numItemRows * rowHeight - scrollBarSize);
                var newScroll = Utils.Clamp((int)Math.Round(ratio * maxScroll), 0, maxScroll);
                SetAndMarkDirty(ref scroll, newScroll);
            }
            else
            {
                PixelToCell(e.X, e.Y, out var row, out var col);
                SetAndMarkDirty(ref hoverRow, row);
                SetAndMarkDirty(ref hoverCol, col);
                SetAndMarkDirty(ref hoverButton, IsPointInButton(e.X, row, col));
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (draggingScrollbars || draggingSlider)
            {
                draggingSlider = false;
                draggingScrollbars = false;
                Capture = false;
                MarkDirty();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            SetAndMarkDirty(ref hoverRow, -1);
            SetAndMarkDirty(ref hoverCol, -1);
            SetAndMarkDirty(ref hoverButton, false);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var sign = e.ScrollY < 0 ? 1 : -1;

            if (sign != 0)
            {
                SetAndMarkDirty(ref scroll, Utils.Clamp(scroll + sign * 3, 0, maxScroll));

                if (dropDownActive != null && dropDownActive.Visible)
                {
                    dropDownActive.SetListOpened(false);
                    dropDownActive.Visible = false;
                    GrabDialogFocus();
                }
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Left && PixelToCell(e.X, e.Y, out var row, out var col) && row < data.GetLength(0))
            {
                CellDoubleClicked?.Invoke(this, row, col);
            }
        }

        private bool GetScrollBarParams(out int pos, out int size)
        {
            if (data != null && data.GetLength(0) > numItemRows)
            {
                var scrollAreaSize = numItemRows * rowHeight;
                var minScrollBarSizeY = scrollAreaSize / 4;
                var scrollY = scroll * rowHeight;
                var maxScrollY = maxScroll * rowHeight;

                size = Math.Max(minScrollBarSizeY, (int)Math.Round(scrollAreaSize * Math.Min(1.0f, scrollAreaSize / (float)(maxScrollY + scrollAreaSize))));
                pos = (int)Math.Round((scrollAreaSize - size) * (scrollY / (float)maxScrollY));

                return true;
            }

            pos = 0;
            size = 0;
            return false;
        }

        protected override void OnRender(Graphics g)
        {
            Debug.Assert(enabled); // TODO : Add support for disabled state.

            var c = parentDialog.CommandList;
            var hasScrollBar = GetScrollBarParams(out var scrollBarPos, out var scrollBarSize);
            var actualScrollBarWidth = hasScrollBar ? scrollBarWidth : 0;

            // Grid lines
            c.DrawLine(0, 0, width, 0, ThemeResources.BlackBrush);
            for (var i = numHeaderRows + 1; i < numRows; i++)
                c.DrawLine(0, i * rowHeight, width - actualScrollBarWidth, i * rowHeight, ThemeResources.BlackBrush);
            for (var j = 0; j < columnOffsets.Length - 1; j++)
                c.DrawLine(columnOffsets[j], 0, columnOffsets[j], height, ThemeResources.BlackBrush);
            if (numHeaderRows != 0)
                c.DrawLine(0, rowHeight, width - 1, rowHeight, ThemeResources.LightGreyFillBrush1);

            // BG
            c.FillAndDrawRectangle(0, 0, width - 1, height, ThemeResources.DarkGreyLineBrush1, ThemeResources.LightGreyFillBrush1);

            var baseY = 0;

            // Header
            if (numHeaderRows != 0)
            {
                c.FillRectangle(0, 0, width, rowHeight, ThemeResources.DarkGreyLineBrush3);
                for (var j = 0; j < columns.Length; j++) 
                    c.DrawText(columns[j].Name, ThemeResources.FontMedium, columnOffsets[j] + margin, 0, ThemeResources.LightGreyFillBrush1, TextFlags.MiddleLeft, 0, rowHeight);
                baseY = rowHeight;
            }

            // Data
            if (data != null)
            {
                // Hovered cell
                if (hoverCol >= 0 && (hoverRow - scroll) >= 0 && hoverRow < data.GetLength(0))
                {
                    if (fullRowSelect)
                        c.FillRectangle(0, (numHeaderRows + hoverRow - scroll) * rowHeight, width, (numHeaderRows + hoverRow - scroll + 1) * rowHeight, ThemeResources.DarkGreyLineBrush3);
                    else
                        c.FillRectangle(columnOffsets[hoverCol], (numHeaderRows + hoverRow - scroll) * rowHeight, columnOffsets[hoverCol + 1], (numHeaderRows + hoverRow - scroll + 1) * rowHeight, ThemeResources.DarkGreyLineBrush3);
                }

                for (int i = 0, k = scroll; i < numItemRows && k < data.GetLength(0); i++, k++) // Rows
                {
                    var y = baseY + i * rowHeight;

                    for (var j = 0; j < data.GetLength(1); j++) // Colums
                    {
                        var col = columns[j];
                        var colWidth = columnWidths[j];
                        var colEnabled = columnEnabled[j];
                        var x = columnOffsets[j];
                        var val = data[k, j];

                        if (k == dropDownRow && j == dropDownCol)
                            continue;

                        c.PushTranslation(x, y);

                        switch (col.Type)
                        {
                            case ColumnType.DropDown:
                            {
                                dropDownInactive.Visible = true;
                                dropDownInactive.SetItems(new[] { (string)val });
                                dropDownInactive.Move(left + x, top + y, columnWidths[j], rowHeight);
                                dropDownInactive.Render(g);
                                dropDownInactive.Visible = false;
                                break;
                            }
                            case ColumnType.Button:
                            {
                                var buttonBaseX = colWidth - rowHeight;
                                c.DrawText((string)val, ThemeResources.FontMedium, margin, 0, ThemeResources.LightGreyFillBrush1, TextFlags.MiddleLeft, 0, rowHeight);
                                c.PushTranslation(buttonBaseX, 0);
                                c.FillAndDrawRectangle(0, 0, rowHeight - 1, rowHeight, hoverRow == k && hoverCol == j && hoverButton ? ThemeResources.MediumGreyFillBrush1 : ThemeResources.DarkGreyLineBrush3, ThemeResources.LightGreyFillBrush1);
                                c.DrawText("...", ThemeResources.FontMediumBold, 0, 0, ThemeResources.LightGreyFillBrush1, TextFlags.MiddleCenter, rowHeight, rowHeight);
                                c.PopTransform();
                                break;
                            }
                            case ColumnType.Slider:
                            {
                                if (colEnabled)
                                {
                                    c.FillRectangle(0, 0, (int)Math.Round((int)val / 100.0f * colWidth), rowHeight, ThemeResources.DarkGreyFillBrush3);
                                    c.DrawText(string.Format(CultureInfo.InvariantCulture, col.StringFormat, (int)val), ThemeResources.FontMedium, 0, 0, ThemeResources.LightGreyFillBrush1, TextFlags.MiddleCenter, colWidth, rowHeight);
                                }
                                else
                                {
                                    c.DrawText("N/A", ThemeResources.FontMedium, 0, 0, ThemeResources.MediumGreyFillBrush1, TextFlags.MiddleCenter, colWidth, rowHeight);
                                }
                                break;
                            }
                            case ColumnType.Label:
                            {
                                c.DrawText((string)val, ThemeResources.FontMedium, margin, 0, ThemeResources.LightGreyFillBrush1, TextFlags.MiddleLeft | (col.Ellipsis ? TextFlags.Ellipsis : 0), colWidth, rowHeight);
                                break;
                            }
                            case ColumnType.CheckBox:
                            {
                                var checkBaseX = (colWidth  - bmpCheckOn.ElementSize.Width)  / 2;
                                var checkBaseY = (rowHeight - bmpCheckOn.ElementSize.Height) / 2;
                                c.PushTranslation(checkBaseX, checkBaseY);
                                c.DrawRectangle(0, 0, bmpCheckOn.ElementSize.Width - 1, bmpCheckOn.ElementSize.Height - 1, ThemeResources.LightGreyFillBrush1);
                                c.DrawBitmapAtlas((bool)val ? bmpCheckOn : bmpCheckOff, 0, 0, 1, 1, Theme.LightGreyFillColor1);
                                c.PopTransform();
                                break;
                            }
                            case ColumnType.Image:
                            {
                                var bmp = g.GetBitmapAtlasRef((string)val);
                                c.DrawBitmapAtlasCentered(bmp, 0, 0, checkBoxWidth, rowHeight, 1, 1, Theme.LightGreyFillColor1);
                                break;
                            }
                        }

                        c.PopTransform();
                    }
                }

                if (hasScrollBar)
                {
                    c.PushTranslation(width - scrollBarWidth - 1, numHeaderRows * rowHeight);
                    c.FillAndDrawRectangle(0, 0, scrollBarWidth, rowHeight * numItemRows, ThemeResources.DarkGreyFillBrush1, ThemeResources.LightGreyFillBrush1);
                    c.FillAndDrawRectangle(0, scrollBarPos, scrollBarWidth, scrollBarPos + scrollBarSize, ThemeResources.MediumGreyFillBrush1, ThemeResources.LightGreyFillBrush1);
                    c.PopTransform();
                }
            }
        }
    }
}