using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Xamarin.Forms;
using Xamarin.Forms.Xaml;
using Camera2Forms.CustomViews;

namespace Camera2Forms
{
	[XamlCompilation(XamlCompilationOptions.Compile)]
	public partial class CameraPage : ContentPage
	{
		public CameraPage ()
		{
			InitializeComponent();
            CameraPreview.PictureFinished += OnPictureFinished;
        }

        void OnCameraClicked(object sender, EventArgs e)
        {
            CameraPreview.CameraClick.Execute(null);
        }

        private void OnPictureFinished()
        {
            DisplayAlert("Confirm", "Picture Taken","","Ok");
        }
    }
}