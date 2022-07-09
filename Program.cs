using CSCore;
using CSCore.Codecs;
using CSCore.CoreAudioAPI;
using CSCore.DirectSound;
using CSCore.SoundOut;
using NoSilence;

int counter = 0;
float maxPeak = 0;

int resetTime = 5;
var fileLocation = string.Empty;
string partOfOutputDevice = string.Empty;
float volumeInPercents = 20;

var manager = new AudioDeviceManager();

UserInput();

using var music = GetMusic();

using var soundOut = GetSoundOut();

soundOut.Initialize(music);

soundOut.Volume = volumeInPercents / 100;

Console.WriteLine();
Console.WriteLine("Initialized successfully");

MainLoop();

void UserInput()
{
    Console.Write("Please input desired volume in percent: ");
    var rawVolume = Console.ReadLine();

    float.TryParse(rawVolume, out var volumeInPercents);

    Console.WriteLine($"Selected {volumeInPercents}%");

    Console.WriteLine();
    Console.Write("Please input full path to sound file: ");
    fileLocation = Console.ReadLine();
    if (File.Exists(fileLocation))
    {
        Console.WriteLine($"File found at path \"{fileLocation}\"");
    }
    else
    {
        return;
    }

    Console.WriteLine();
    Console.WriteLine("Please input uniquely identifying part of the name of the device from below list [Do not use primary device]");
    Console.WriteLine();
    Console.WriteLine(string.Join(", ", DirectSoundDeviceEnumerator.EnumerateDevices().Select(d => $"\"{d.Description}\"")));

    Console.WriteLine();
    Console.Write("Part of name: ");
    partOfOutputDevice = Console.ReadLine();

}

void MainLoop()
{
    try
    {
        while (true)
        {
            using var meter = AudioMeterInformation.FromDevice(manager.GetDefaultDevice());

            maxPeak = meter.PeakValue > maxPeak ? meter.PeakValue : maxPeak;

            if (counter == resetTime)
            {
                ChangePlayState();

                maxPeak = 0;
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

void ChangePlayState()
{
    if (maxPeak > 0)
    {
        if (soundOut.PlaybackState != PlaybackState.Paused)
        {
            Console.WriteLine("Noise detected pausing music");

            soundOut.Pause();
        }
    }
    else
    {
        if (soundOut.PlaybackState != PlaybackState.Playing)
        {
            Console.WriteLine("Noise stopped starting music");

            soundOut.Play();
        }
    }
}

ISoundOut GetSoundOut()
{
    var devices = DirectSoundDeviceEnumerator.EnumerateDevices();

    var backgroundNoiseDevice = devices.First(d => d.Description.Contains(partOfOutputDevice, StringComparison.InvariantCultureIgnoreCase));

    var directCount = new DirectSoundOut();

    directCount.Device = backgroundNoiseDevice.Guid;

    return directCount;
}

IWaveSource GetMusic()
{
    return CodecFactory.Instance.GetCodec(fileLocation);
}
