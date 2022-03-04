using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TextureLuminanceImportanceSampling : MonoBehaviour
{
    // Input sky/probe texture, assign this in the editor
    public Texture2D InputTexture;

    // Sample count, an average sample count for RTGI is 8
    public int SampleCount = 1024;

    // Disabling this will force random uniform samples
    public bool ImportanceSamplingEnabled = true;

    // Draw visible samples to texture in the scene (comes with a performance impact)
    public bool DrawSamplesEnabled = true;

    // If we're drawing visible samples to the texture in the scene, specify the pixel size per sample
    public int DrawSamplesPixelSize = 3;

    // This allows for sampling the texture at a lower resolution compared to the source image, but it is not recommended due to possible inaccuracy for small lights
    public int textureCaptureWidth = 1024;
    public int textureCaptureHeight = 512;

    // Source texture instance to avoid writing over the original texture
    private Texture2D textureInstance;

    // We will cache our texture instance width and height to avoid reading the texture every frame
    private int textureInstanceWidth;
    private int textureInstanceHeight;

    // We will store the texture total luminance for calculating the correct PDF
    private float textureTotalLuminance = 0.0f;

    // Multidimensional RGB table (X + Y) to avoid texture lookup every frame, this is not required in a possible HLSL version
    private ColorRow[] textureRGB;

    // Multidimensional (X + Y) table to store luminance per pixel
    private FloatRow[] textureLuminance;

    // Container where we will store both Y and YX CDF
    private CDFContainer textureCDF;

    ///<summary>
    /// Emulating the random owen scrambled sample, returns a float between 0 and 1
    ///</summary>
    float GetBNDSequenceSample()
    {
        return Random.Range(0.0f, 1.0f);
    }

    ///<summary>
    /// Multidimensional (X + Y) Color (RGB) table stored as Vector3 (XYZ)
    ///</summary>
    struct ColorRow
    {
        // The length of the colors array below, will be the amount of pixels on the X axis (textureCaptureWidth)
        public Vector3[] colors;
    }

    ///<summary>
    /// Multidimensional (X + Y) table to store floats, used for Luminance & CDF
    ///</summary>
    struct FloatRow
    {
        // The length of the floats array below, will be the amount of pixels on the X axis (textureCaptureWidth)
        public float[] floats;
    }

    ///<summary>
    /// Emulating the (2D, X + Y version) GGXSample/RaytracingSample
    ///</summary>
    struct RaytracingSample
    {
        public float x;
        public float y;
        public float pdf;
    }

    ///<summary>
    /// Container that will contain both our CDFY an CDFYX tables
    ///</summary>
    struct CDFContainer
    {
        // The length of the CDF Y table will always be textureCaptureHeight (1 dimensional)
        public float[] CDFY;

        // The lengt hof the CDF YX will always be textureCaptureHeight, however, the floats array inside will be of textureCaptureWidth (2 dimensional)
        public FloatRow[] CDFYX;
    }


    ///<summary>
    /// Calculates the pixel luminance of a given X and Y in our RGB color map
    ///</summary>
    float GetTexturePixelLuminance(ColorRow[] _textureRGB, int _y, int _x)
    {
        // We calculate the pixel luminance by summing R + G + B, an alternative to this would be textureRGB[i].colors[j].magnitude, howevever, this is faster
        float _pixelLuminance = _textureRGB[_y].colors[_x].x + _textureRGB[_y].colors[_x].y + _textureRGB[_y].colors[_x].z;

        // An alternative to this would be pow(_pixelLuminance, 2), however, this is faster
        _pixelLuminance *= _pixelLuminance;

        return _pixelLuminance;
    }

    ///<summary>
    /// Populates the RGB color map, not required in HLSL when sampling directly from the sky/probe
    ///</summary>
    void PopulateColorMap(Texture2D _inputTexture, ref ColorRow[] _textureRGB)
    {
        // Calculate texture scale, compared to input image source
        float _xScale = (float)textureCaptureWidth / textureInstanceWidth;
        float _yScale = (float)textureCaptureHeight / textureInstanceHeight;

        // When using a 1024px source image, but sampling our texture at 512px, our scale will be 0.5
        // This will ensure that pixel 128 on the sampled texture, will be retrieved from pixel 256 on the source image
        for (int i = 0; i < _textureRGB.Length; i++) // Height (Y)
        {
            // We create an array of colors in this color row, the amount will be the total width pixels of the capture
            _textureRGB[i].colors = new Vector3[textureCaptureWidth];

            for (int j = 0; j < _textureRGB[i].colors.Length; j++) // Width (X)
            {
                // Get this pixel color on the source image
                Color _pixelColor = _inputTexture.GetPixel(
                    Mathf.RoundToInt(j / _xScale), // Width
                    Mathf.RoundToInt(i / _yScale) // Height
                );

                // Store our final color in the RGB color map
                _textureRGB[i].colors[j].x = _pixelColor.r;
                _textureRGB[i].colors[j].y = _pixelColor.g;
                _textureRGB[i].colors[j].z = _pixelColor.b;
            }
        }
    }

    ///<summary>
    /// Populates the Luminance map, by going through every pixel on the specified RGB color map and computing the luminance value for it
    ///</summary>
    void PopulateLuminanceMap(ColorRow[] _textureRGB, ref float _textureTotalLuminance, ref FloatRow[] _textureLuminance)
    {
        // Create a variable to store our total luminance for this RGB map
        _textureTotalLuminance = 0.0f;

        for (int i = 0; i < textureCaptureHeight; i++) // Height (Y)
        {
            // We create an array of floats (X axis) in this float row (Y axis), the amount will be the width of the texture capture
            if (_textureLuminance[i].floats == null)
                _textureLuminance[i].floats = new float[textureCaptureWidth];

            for (int j = 0; j < textureCaptureWidth; j++) // Width (X)
            {
                // Get pixel luminance at current coordinate
                float _pixelLuminance = GetTexturePixelLuminance(_textureRGB, i, j); // Height, width

                // Store pixel luminance at correct coordinate in the Y X luminance map
                _textureLuminance[i].floats[j] = _pixelLuminance;

                // Contribute to total luminance
                _textureTotalLuminance += _pixelLuminance;
            }
        }
    }

    ///<summary>
    /// Populates the CDF Y and CDF YX in the specified CDFContainer, by calculating the PDF and cumulative summing for every texture capture pixel
    ///</summary>
    void PopulateCDFMap(FloatRow[] _textureLuminance, ref CDFContainer _cdfContainer)
    {
        // For the CDF we need to keep track of the sum, so we create two values starting at zero
        float _cdfYSum = 0.0f;
        float _CDFYXSum = 0.0f;

        // We will start by building the CDFY, the array length will be the height of the texture capture (because we are currently on the Y axis)
        if (_cdfContainer.CDFY == null)
            _cdfContainer.CDFY = new float[textureCaptureHeight];

        // For each row on the luminance map (iterating on the Y axis of our luminance map), we will count all children (left to right on x axis, for that row)
        for (int i = 0; i < textureCaptureHeight; i++)
        {
            // Sum all luminance on this row, divide by total image luminance to get the correct PDF
            for (int j = 0; j < textureCaptureWidth; j++)
            {
                // Cumulative sum the calculated PDF
                _cdfYSum += _textureLuminance[i].floats[j] / textureTotalLuminance;
            }

            // Store the cumulative summed value for this (X, width) row, on the correct coordinate (Y, height) position in our CDF Y
            _cdfContainer.CDFY[i] = _cdfYSum;
        }

        // Now we will build the CDFYX, we will create x amount of FloatRows, x being the height of the luminance map (every possible height will get its own index on the CDFYX)
        if (_cdfContainer.CDFYX == null)
            _cdfContainer.CDFYX = new FloatRow[textureCaptureHeight];

        // We will now loop through every pixel on the Y axis (height), because we need to create a new CDF (X, width) row, for every possible input (Y, height)
        for (int i = 0; i < textureCaptureHeight; i++)
        {
            // Reset CDF Y X sum, because we will start over, cumulative summing from 0 for every possible Y axis we process
            _CDFYXSum = 0.0f;

            // We create a float[] array on this floatrow as a container to hold all the floats (CDF from left to right, on the X/width axis), so the length of the array will be the width of the texture capture
            if (_cdfContainer.CDFYX[i].floats == null)
                _cdfContainer.CDFYX[i].floats = new float[textureCaptureWidth];

            // Create a container to store the total luminance of this row on the X axis (width)
            float _rowTotalLuminance = 0.0f;

            // Calculate the total luminance for this row on the X axis (width)
            for (int j = 0; j < textureCaptureWidth; j++)
            {
                // Retrieve the pixel luminance at the current X Y value in the luminance map, and contribute to the total luminance of this row (on the X axis, width)
                _rowTotalLuminance += _textureLuminance[i].floats[j];
            }

            // Now that we've calculated the total luminance of this row (from left to right), we need to loop through the X axis again and use that value to calculate the PDF for every pixel
            for (int j = 0; j < textureCaptureWidth; j++)
            {
                // We will take the luminance of this pixel, divide by this row (from left to right) total luminance we just calculated, to calculate the pixel PDF, and cumulative sum
                _CDFYXSum += _textureLuminance[i].floats[j] / _rowTotalLuminance;

                // Store the CDF Y X at the correct height input (where we currently are at the Y axis, height)
                _cdfContainer.CDFYX[i].floats[j] = _CDFYXSum;
            }
        }
    }

    ///<summary>
    /// Will return the closest index for a given 0..1 float coordinate in a CDF
    ///</summary>
    int GetClosestIndexInCDF(float _input, float[] _inputArray)
    {
        // Loop through the haystack
        for (int i = 0; i < _inputArray.Length; i++)
        {
            // Since we started searching from the bottom, we can just check if the value we found is the same / higher than we're looking for, this is the best sample
            if (_inputArray[i] >= _input)
                return i;
        }

        // We did not find a fitting sample
        return 0;
    }

    ///<summary>
    /// Generates x number of random ray tracing samples
    ///</summary>
    RaytracingSample[] GenerateUniformRandomSamples(int _sampleCount)
    {
        // Create array to store our random samples
        RaytracingSample[] _randomSamples = new RaytracingSample[_sampleCount];

        // Loop through specified sample count and create a new random sample
        for (int i = 0; i < _sampleCount; i++)
        {
            _randomSamples[i] = new RaytracingSample();
            _randomSamples[i].x = GetBNDSequenceSample();
            _randomSamples[i].y = GetBNDSequenceSample();
            _randomSamples[i].pdf = 1.0f;
        }

        return _randomSamples;
    }

    ///<summary>
    /// Takes an array of random ray tracing samples, and will importance sample them according to the input CDF
    ///</summary>
    void ImportanceSamples(ref RaytracingSample[] _samples, CDFContainer _textureCDF)
    {
        for (int i = 0; i < _samples.Length; i++)
        {
            // Transform the (random) sample into an importance sampled sample
            ImportanceSample(ref _samples[i], _textureCDF);
        }
    }

    ///<summary>
    /// Takes a random ray tracing sample, and importance samples it according to the input CDF
    ///</summary>
    void ImportanceSample(ref RaytracingSample _sample, CDFContainer _textureCDF)
    {
        // Importance sample the y axis, by looking at the closest entry on the CDF Y axis for our sample Y
        int _CDFY = GetClosestIndexInCDF(_sample.y, _textureCDF.CDFY);

        // The result is an int index from the array (which has the length of our texture height), so by dividing by the texture height, we can transform back to a 0..1 float coordinate
        _sample.y = (float) _CDFY / (float) textureCaptureHeight;

        // We will use the Y index we got from importance sampling the Y (texture height) axis, as input to importance sample the X (texture width) coordinate, for a given Y X
        int _CDFYX = GetClosestIndexInCDF(_sample.x, _textureCDF.CDFYX[_CDFY].floats);

        // We will also use the actual texture capture width to transform this int index on the texture back to a 0..1 float coordinate
        _sample.x = (float) _CDFYX / (float) textureCaptureWidth;

        // We will calculate and store the correct PDF for this sample, by taking the pixel luminance of this sample and dividing it by the total texture luminance
        _sample.pdf = textureLuminance[_CDFY].floats[_CDFYX] / textureTotalLuminance;
    }

    ///<summary>
    /// Takes an array of ray tracing samples as input and draws them over the specified texture2d
    ///</summary>
    void DrawSamples(RaytracingSample[] _inputSamples, Texture2D _textureInstance)
    {
        // Loop through all the passed samples
        for (int i = 0; i < _inputSamples.Length; i++)
        {
            // Write pixel width for our globally specified pixel size
            for (int pixelIndexX = 0; pixelIndexX < DrawSamplesPixelSize; pixelIndexX++)
            {
                // Write pixel height for our globally specified pixel size
                for (int pixelIndexY = 0; pixelIndexY < DrawSamplesPixelSize; pixelIndexY++)
                {
                    // Convert our 0..1 float sample coordinates back to coordinates on the texture instance, substract half the pixel width to make sure we're drawing from the middle of the pixel
                    _textureInstance.SetPixel(
                        Mathf.RoundToInt((_inputSamples[i].x * textureInstanceWidth) + pixelIndexX) - (DrawSamplesPixelSize / 2), // X
                        Mathf.RoundToInt((_inputSamples[i].y * textureInstanceHeight) + pixelIndexY) - (DrawSamplesPixelSize / 2), // Y
                        Color.white
                    );
                }
            }
        }

        // Apply all written pixels to texture instance
        textureInstance.Apply();
    }

    ///<summary>
    /// Called once at startup
    ///</summary>
    private void Awake()
    {
        // Store an instance of our texture, to avoid writing to the texture on disk
        textureInstance = new Texture2D(InputTexture.width, InputTexture.height, TextureFormat.RGBA32, false);
        Graphics.CopyTexture(InputTexture, 0, 0, textureInstance, 0, 0);

        // Assign this texture instance to the material in the scene
        GetComponent<Renderer>().material.mainTexture = textureInstance;

        // We also store/cache the dimensions of the source texture, to avoid texture lookups every frame
        textureInstanceWidth = textureInstance.width;
        textureInstanceHeight = textureInstance.height;

        // Populate RGB color map, which is also done to avoid reading the source texture every frame
        textureRGB = new ColorRow[textureCaptureHeight];
        PopulateColorMap(textureInstance, ref textureRGB);

        // Create an empty Y X container to store our luminance Y X table, which will be populated later
        textureLuminance = new FloatRow[textureCaptureHeight];
    }

    ///<summary>
    /// Called every frame
    ///</summary>
    void Update()
    {
        // Generate random uniform samples
        RaytracingSample[] _samples = GenerateUniformRandomSamples(SampleCount);

        if (ImportanceSamplingEnabled)
        {
            // Compute the luminance table from our input texture (cached pixel RGB table in this case)
            PopulateLuminanceMap(textureRGB, ref textureTotalLuminance, ref textureLuminance);

            // Compute CDF for both Y and YX axis and store in our CDF container
            PopulateCDFMap(textureLuminance, ref textureCDF);

            // Use the computed CDF to importance sample the random samples we generated
            ImportanceSamples(ref _samples, textureCDF);
        }

        if (DrawSamplesEnabled)
        {
            // Re-copy the source texture to the texture instance to go back to original state (no visible samples)
            Graphics.CopyTexture(InputTexture, 0, 0, textureInstance, 0, 0);

            // Draw samples over texture instance
            DrawSamples(_samples, textureInstance);
        }
    }
}
