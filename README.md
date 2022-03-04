# HDRI/texture luminance importance sampling in Unity C#

![Preview](preview.jpg)

Open Scenes/Example and hit play. To enable/disable importance sampling, select the Texture gameobject in the scene and toggle "Importance Sampling Enabled" on the component.

## Extra information

Instead of multidimensional arrays, you could use linear single dimensional arrays. To obtain the pixel index for a given X Y coordinate, use the following.

```C#
int pixelIndex = TextureCaptureWidth * y + x;
```

## Links

Rathaus HDRI (CC0) downloaded from [here](https://polyhaven.com/a/rathaus)

Special thanks to [this reddit thread](https://www.reddit.com/r/GraphicsProgramming/comments/lqr5u5/how_should_i_sample_an_hdri/) for providing information on the subject