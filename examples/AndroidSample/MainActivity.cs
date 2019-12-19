﻿using System;

using Android.App;
using Android.Content.PM;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Android.OS;

namespace AndroidSample
{
    [Activity(Label = "AndroidSample", Icon = "@drawable/icon", Theme = "@style/MainTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation)]
    public class MainActivity : global::Xamarin.Forms.Platform.Android.FormsAppCompatActivity
    {
        private string clientId = "Android-" + Guid.NewGuid().ToString().Split("-")[0];

        static void HandleExceptions(object sender, UnhandledExceptionEventArgs e)
        {
            //Exception d = (Exception)e.ExceptionObject;
            Android.Util.Log.Debug("ably", "App error: " + e.ExceptionObject.ToString());
        }

        public MainActivity()
        {
            AppDomain currentDomain = AppDomain.CurrentDomain;
            currentDomain.UnhandledException += HandleExceptions;
        }

        public AblyService ablyService = null;
        protected override void OnCreate(Bundle bundle)
        {
            TabLayoutResource = AndroidSample.Resource.Layout.Tabbar;
            ToolbarResource = AndroidSample.Resource.Layout.Toolbar;

            base.OnCreate(bundle);

            global::Xamarin.Forms.Forms.Init(this, bundle);
            ablyService = new AblyService();
            ablyService.Init(clientId);
            LoadApplication(new App(ablyService));
        }
    }
}

