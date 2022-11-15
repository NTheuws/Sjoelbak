using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Intel.RealSense;
using Stream = Intel.RealSense.Stream;
using System.Windows.Threading;
using System.Diagnostics;

namespace DistRS
{
    public partial class MainWindow : Window
    {
        private Pipeline pipe;
        private Colorizer colorizer;
        private CancellationTokenSource tokenSource = new CancellationTokenSource();

        const int height = 240;
        const int width = 320;
        const int arraySize = ((320 / 5) * (240 / 5));
        float[] distArray = new float[arraySize];
        float[] callibrationArray = new float[arraySize];

        int callibrationClickCount = 0;
        Point callibrationTopLeft = new Point(0f, 0f);
        Point callibrationBottomRight = new Point(320f, 240f);
        bool callibrationCornersSet = false;


        private SerialCommunication com;
        int pixelCount = 0;

        static Action<VideoFrame> UpdateImage(System.Windows.Controls.Image img)
        {
            var bmap = img.Source as WriteableBitmap;
            return new Action<VideoFrame>(frame =>
            {
                var rect = new Int32Rect(0, 0, frame.Width, frame.Height);
                bmap.WritePixels(rect, frame.Data, frame.Stride * frame.Height, frame.Stride);
            });
        }
        public MainWindow()
        {
            InitializeComponent();

            try
            {
                Action<VideoFrame> updateDepth;
                Action<VideoFrame> updateColor;

                // The colorizer processing block will be used to visualize the depth frames.
                colorizer = new Colorizer();

                // Create and config the pipeline to strem color and depth frames.
                pipe = new Pipeline();

                using (var ctx = new Context())
                {
                    var devices = ctx.QueryDevices();
                    var dev = devices[0];

                    Console.WriteLine("\nUsing device 0, an {0}", dev.Info[CameraInfo.Name]);
                    Console.WriteLine("    Serial number: {0}", dev.Info[CameraInfo.SerialNumber]);
                    Console.WriteLine("    Firmware version: {0}", dev.Info[CameraInfo.FirmwareVersion]);

                    var sensors = dev.QuerySensors();
                    var depthSensor = sensors[0];
                    var colorSensor = sensors[1];

                    var depthProfile = depthSensor.StreamProfiles
                                        .Where(p => p.Stream == Stream.Depth)
                                        .OrderBy(p => p.Framerate)
                                        .Select(p => p.As<VideoStreamProfile>()).First();

                    var colorProfile = colorSensor.StreamProfiles
                                        .Where(p => p.Stream == Stream.Color)
                                        .OrderBy(p => p.Framerate)
                                        .Select(p => p.As<VideoStreamProfile>()).First();

                    var cfg = new Config();
                    cfg.EnableStream(Stream.Depth, 320, 240, depthProfile.Format, depthProfile.Framerate);
                    cfg.EnableStream(Stream.Color, colorProfile.Width, colorProfile.Height, colorProfile.Format, colorProfile.Framerate);


                    var pp = pipe.Start(cfg);

                    SetupWindow(pp, out updateDepth, out updateColor);
                }
                Task.Factory.StartNew(() =>
                {
                    while (!tokenSource.Token.IsCancellationRequested)
                    {
                        using (var frames = pipe.WaitForFrames())
                        {
                            var colorFrame = frames.ColorFrame.DisposeWith(frames);
                            var depthFrame = frames.DepthFrame.DisposeWith(frames);

                            var colorizedDepth = colorizer.Process<VideoFrame>(depthFrame).DisposeWith(frames);

                            Dispatcher.Invoke(DispatcherPriority.Render, updateDepth, colorizedDepth);
                            Dispatcher.Invoke(DispatcherPriority.Render, updateColor, colorFrame);

                            Dispatcher.Invoke(new Action(() =>
                            {
                                String depth_dev_sn = depthFrame.Sensor.Info[CameraInfo.SerialNumber];
                            }));
                        }
                    }
                }, tokenSource.Token);
            }
            catch (Exception)
            {
                Application.Current.Shutdown();
            }
        }

        private void Control_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            tokenSource.Cancel();
        }

        private void SetupWindow(PipelineProfile pipelineProfile, out Action<VideoFrame> depth, out Action<VideoFrame> color)
        {
            using (var vsp = pipelineProfile.GetStream(Stream.Depth).As<VideoStreamProfile>())
                imgDepth.Source = new WriteableBitmap(vsp.Width, vsp.Height, 96d, 96d, PixelFormats.Rgb24, null);
            depth = UpdateImage(imgDepth);

            using (var vsp = pipelineProfile.GetStream(Stream.Color).As<VideoStreamProfile>())
                imgColor.Source = new WriteableBitmap(vsp.Width, vsp.Height, 96d, 96d, PixelFormats.Rgb24, null);
            color = UpdateImage(imgColor);
        }

        private void ButtonReadDist_Click(object sender, RoutedEventArgs e)
        {
            using (var frames = pipe.WaitForFrames())
            using (var depth = frames.DepthFrame)
            {
                int x = int.Parse(Xcoordinate.Text) - 1;
                int y = int.Parse(Ycoordinate.Text) - 1;
                Console.WriteLine("The camera is pointing at an object " +
                    depth.GetDistance(x, y) + " meters away\t");
                tbResult.Text = "Distance is " + depth.GetDistance(x, y) + " m";
                depth.Dispose();
                frames.Dispose();
            }
        }

        // Create a callibration distance array
        private void ButtonCallibrate_Click(object sender, RoutedEventArgs e)
        {
            CheckPixels(callibrationArray);
            tbDotCount.Text = "Callibration done.";
        }
        // Compare current to callibration
        private void ButtonCompare_Click(object sender, RoutedEventArgs e)
        {
            CheckPixels(distArray);
            ComparePixels();
        }

        private float GetDistance(int x, int y)
        {
            float num = 0;
            using (var frames = pipe.WaitForFrames())
            using (var depth = frames.DepthFrame)
            {
                num = depth.GetDistance(x, y);
                depth.Dispose();
                frames.Dispose();
            }
            return num;
        }

        private void CheckPixels(float[] array)
        {
            int num = -1;

            using (var frames = pipe.WaitForFrames())
            using (var depth = frames.DepthFrame)
            {
                for (int x = (int)callibrationTopLeft.X; x < callibrationBottomRight.X; x++) // Check Width 320/10 pixels.
                {
                    for (int y = (int)callibrationTopLeft.Y; y < callibrationBottomRight.Y; y++) // Check Height 240/10 pixels.
                    {
                        //if (x % 5 == 0 && y % 5 == 0)
                        //{
                            num++;
                            array[num] = depth.GetDistance(x, y);
                        //}
                    }
                }
                depth.Dispose();
                frames.Dispose();
            }
        }

        private void ComparePixels()
        {
            // 768 pixels 32x24.
            // Y-axis first followed by the X-axis. Top left to bottom left then moving one to the right.
            pixelCount = 0; // Count of the amount of pixels that are closer than in the callibration.
            int x = 0; // Keep track of the x coordinate of the current pixel.
            int y = 0;  // Keep track of the y coordinate of the current pixel.
            
            Rectangle[,] rectangles = new Rectangle[(int)(callibrationBottomRight.X - callibrationTopLeft.X) +1, (int)(callibrationBottomRight.Y - callibrationTopLeft.Y) + 1];

            // Start by clearing the last canvas.
            CanvasMap.Children.Clear();

            for (int i = 0; i < callibrationArray.Length; i++)
            { 
                // Count amount of pixels that are closer now compared to before.
                if ( callibrationArray[i] != 0 && distArray[i] != 0 && distArray[i] + 0.01f < callibrationArray[i])
                {
                    pixelCount++;
                    // Save for the drawing.
                    //int x1 = x;
                    int ymax = (int)(callibrationBottomRight.Y - callibrationTopLeft.Y) + 1;
                    int y1 = y;

                    rectangles[x, y] =
                        new Rectangle()
                        {
                            Width = 10,
                            Height = 10,
                            Fill = Brushes.Red,
                            RenderTransform = new TranslateTransform(x * 2, y * 2)

                        };
                    // Draw the map.
                    CanvasMap.Children.Add(rectangles[x, y]);
                }
                y++;
                // Go through it row to row
                if ((i + 1) % (callibrationBottomRight.Y - callibrationTopLeft.Y) == 0)
                {
                    y = 0;
                    x++;
                }
            }
            Console.WriteLine(x + "/" + y);

            // Move 

            tbDotCount.Text = pixelCount.ToString() + " / " + callibrationArray.Length;
        }

        // Connect to the arduino and start playing.
        private void ButtonConnect_Click(object sender, RoutedEventArgs e)
        {
            com = new SerialCommunication();
        }

        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Point p = Mouse.GetPosition(CanvasMap);
            p.X = Math.Round(p.X / 2);
            p.Y = Math.Round(p.Y / 2);
            
            switch (callibrationClickCount)
            {
                case 0: // First click.
                    callibrationTopLeft = p;
                    break;
                case 1: // Second click.
                    callibrationBottomRight = p;
                    callibrationCornersSet = true;
                    Console.WriteLine(callibrationTopLeft + "/" + callibrationBottomRight);
                    int newArraySize = (int)((callibrationBottomRight.X - callibrationTopLeft.X)*(callibrationBottomRight.Y - callibrationTopLeft.Y));
                    distArray = new float[newArraySize];
                    callibrationArray = new float[newArraySize];

                    // Move canvas to the right place.
                    //

                    break;
            }
            callibrationClickCount++; 
        }
    }
}