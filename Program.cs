using CSCore;
using CSCore.Codecs;
using CSCore.CoreAudioAPI;
using CSCore.DirectSound;
using CSCore.SoundOut;
using NoSilence;

int counter = 0;

float volumeThreshold = 0.01f;
int resetTime = 5; //*100ms
var fileLocation = string.Empty;
string? partOfOutputDevice = string.Empty;
float volumeInPercents = 20;

var manager = new AudioDeviceManager();

if (args.Length > 0)
{
    if (args[0].Contains("help", StringComparison.InvariantCultureIgnoreCase)) {

        Console.WriteLine("Either specify arguments, or let the inbuilt setup guide run if you don't specify any. Make sure your primary device is the one you are listening to normally ie with headphones. The one you select will be the music output. They cannot be the same as the program will trigger itself.");
        Console.WriteLine();
        Console.WriteLine("Arguments are --volume 0-100");
        Console.WriteLine("Arguments are --file C:\\test.mp3 (can also be relative path so if the sound file is next to the mp3 just do test.mp3)");
        Console.WriteLine("Arguments are --device SAMS (can be part of the name of the sound device, for example given this will match to SAMSUNG TV");

        return;
    } 
    else
    {
        if(args.Length != 6)
        {
            Console.WriteLine("Wrong number of parameters provided");
        }

        //0    1        2  3      4           5        6
        //.exe --volume 20 --file C:\file.mp3 --device SAMSUNG (any order works)
        for (int i = 0; i < 3; i ++)
        {
            CheckForPair(i * 2);
        }
    }
} 
else
{
    UserInput();
}

using var music = GetMusic();

using var soundOut = GetSoundOut();

soundOut.Initialize(music);

soundOut.Stopped += OnSoundStopped;

soundOut.Volume = volumeInPercents / 100;

Console.WriteLine();
Console.WriteLine("Initialized successfully");

MainLoop();

void CheckForPair(int i)
{
    switch (args[i])
    {
        case "--volume":
            volumeInPercents = int.Parse(args[i + 1]);
            break;
        case "--file":
            fileLocation = args[i + 1];
            break;
        case "--device":
            partOfOutputDevice = args[i + 1];
            break;
        default:
            Console.WriteLine($"Parameter {args[i]} is named incorrectly make sure it is spelled right.");
            return;
    }
}

void UserInput()
{
    Console.Write("Please input desired volume in percent: ");
    var rawVolume = Console.ReadLine();

    float.TryParse(rawVolume, out var volumeInPercents);

    Console.WriteLine($"Selected {volumeInPercents}%");

    Console.WriteLine();
    Console.Write("Please input full path to sound file: ");

    do
    {
        Console.Write("Please input full path to sound file: ");

        fileLocation = Console.ReadLine();
        if (File.Exists(fileLocation))
        {
            Console.WriteLine($"File found at path \"{fileLocation}\"");

            break;
        } else
        {
            Console.WriteLine($"Error: file not found at path \"{fileLocation}\"");
        }

    } while (true);

    Console.WriteLine();
    Console.WriteLine("Please input uniquely identifying part of the name of the device from below list [Do not use primary device]");
    Console.WriteLine();
    Console.WriteLine(string.Join(", ", DirectSoundDeviceEnumerator.EnumerateDevices().Select(d => $"\"{d.Description}\"")));

    Console.WriteLine();
    Console.Write("Part of name: ");
    partOfOutputDevice = Console.ReadLine();

}

void OnSoundStopped(object? sender, PlaybackStoppedEventArgs e)
{
    soundOut.Play();
}

void MainLoop()
{
    try
    {
        while (true)
        {
            if (counter == resetTime)
            {
                AttemptChangingPlayState();

                counter = 0;
            }

            counter++;

            Thread.Sleep(100);
        }
    }
    finally
    {
        soundOut.Stop();
    }
}

void AttemptChangingPlayState()
{
    using var meter = AudioMeterInformation.FromDevice(manager.GetDefaultDevice());

    if (meter.PeakValue > volumeThreshold)
    {
        Thread.Sleep(3000);

        if (meter.PeakValue > volumeThreshold && soundOut.PlaybackState != PlaybackState.Paused)
        {
            Console.WriteLine($"Noise detected pausing music. Peak: {meter.PeakValue}");

            soundOut.Pause();
        }
    }
    else
    {
        if (soundOut.PlaybackState != PlaybackState.Playing)
        {
            Console.WriteLine($"Noise stopped starting music. Peak: {meter.PeakValue}");

            soundOut.Play();
        }
    }
}

ISoundOut GetSoundOut()
{
    var devices = DirectSoundDeviceEnumerator.EnumerateDevices();

    var backgroundNoiseDevice = devices.First(d => d.Description.Contains(partOfOutputDevice, StringComparison.InvariantCultureIgnoreCase));

    Console.WriteLine($"You have selected: {backgroundNoiseDevice.Description}");

    var directCount = new DirectSoundOut();

    directCount.Device = backgroundNoiseDevice.Guid;

    return directCount;
}

IWaveSource GetMusic()
{
    return CodecFactory.Instance.GetCodec(fileLocation);
}
