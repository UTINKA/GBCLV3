﻿using System.Windows.Media.Imaging;

namespace GBCLV3.Models.Auxiliary
{
    public class Skin
    {
        public bool IsSlim { get; set; }

        public BitmapImage Body { get; set; }

        public BitmapImage Cape { get; set; }

        public BitmapSource Face { get; set; }
    }
}
