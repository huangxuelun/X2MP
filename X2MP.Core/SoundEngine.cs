﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace X2MP.Core
{
    public class SoundEngine : IDisposable, INotifyPropertyChanged
    {
        #region Events

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<NeedNextMediaEventArgs> NeedNextMedia;

        #endregion

        #region Fields

        public enum PlaybackControl
        {
            Neutral,
            Pause,
            Stop,
            Seek,
            Next,
            Prev
        }

        /// <summary>
        /// FMOD system object
        /// </summary>
        private FMOD.System _system;
        private FMOD.Sound _sound;
        private FMOD.Channel _channel;
        private FMOD.DSP _dsp;
        FMOD.DSP_DESCRIPTION dspDesc;

        ///// <summary>
        ///// Stores information to control playback. This structure will be passed to callbacks
        ///// </summary>
        //private SoundSystemControl _control;

        ///// <summary>
        ///// Handle to the SoundSystemControl object
        ///// </summary>
        //private GCHandle _controlHandle;

        /// <summary>
        /// Cancellation token source for cancelling playback
        /// </summary>
        private CancellationTokenSource _playbackCts;

        /// <summary>
        /// Playing channel
        /// </summary>
        //private FMOD.Channel _channel;
        //private List<FMOD.Channel> Channels { get; set; }

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets the internal playlist from which FMOD gets its streams
        /// </summary>
        private List<MediaInfo> InternalPlayList { get; set; }

        /// <summary>
        /// Gets the currently playing playlist.
        /// </summary>
        public PlayList NowPlaying { get; private set; }


        ///// <summary>
        ///// Gets whether the channel is playing
        ///// </summary>
        //public bool IsPlaying
        //{
        //    get
        //    {
        //        if (_channel != null)
        //        {

        //            bool isPlaying;
        //            FMOD.RESULT result;

        //            result = _channel.isPlaying(out isPlaying);
        //            CheckError(result);

        //            //returns whether or not the channel is playing
        //            return isPlaying;
        //        }
        //        else
        //        {
        //            return false;
        //        }
        //    }
        //}
        public bool IsPlaying { get; private set; }
        private PlaybackControl PlayControl { get; set; }

        /// <summary>
        /// Gets the length of the media in milliseconds
        /// </summary>
        private uint _length;
        public uint Length
        {
            get { return _length; }
            private set
            {
                _length = value;

                OnPropertyChanged("Length");
            }
        }

        /// <summary>
        /// Sets the position of the media in milliseconds
        /// </summary>
        private uint _position;
        public uint Position
        {
            get { return InternalPosition; }
            set
            {
                InternalPosition = value;

                //set position on the channel
                SetPostion(value);
            }
        }

        private uint InternalPosition
        {
            get
            {
                return _position;
            }
            set
            {
                _position = value;

                OnPropertyChanged("Position");
            }
        }

        #endregion

        #region Constructor / Destructor

        /// <summary>
        /// Start the FMOD sound system engine
        /// </summary>
        public SoundEngine()
        {
            //initialize the playlist
            NowPlaying = new PlayList();
            //initialize internal playlist
            InternalPlayList = new List<MediaInfo>();
            //playing channels
            _playbackCts = new CancellationTokenSource();


            FMOD.RESULT result;

            //create the system object   
            result = FMOD.Factory.System_Create(out _system);
            CheckError(result);

            //initialize fmod
            result = _system.init(32, FMOD.INITFLAGS.NORMAL, IntPtr.Zero);
            CheckError(result);
            dspDesc = new FMOD.DSP_DESCRIPTION();

            //create our dsp description
            dspDesc.name = new char[32];
            dspDesc.version = 0x00010000;
            dspDesc.numinputbuffers = 1;
            dspDesc.numoutputbuffers = 1;
            dspDesc.read = myDSPCallback;

            ////create the dsp, although it will not be active at this time
            //result = _system.createDSP(ref dspDesc, out _dsp);
        }

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose()
        {
            //destroy FMOD ';;'
            if (_system != null)
            {
                FMOD.RESULT result;

                //close system
                result = _system.close();
                CheckError(result);

                //release system
                result = _system.release();
                CheckError(result);

            }
        }

        #endregion

        #region Callbacks
        FMOD.RESULT myDSPCallback(ref FMOD.DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
        {
            unsafe
            {

                float* outBuff = (float*)outbuffer.ToPointer();
                float* inBuff = (float*)inbuffer.ToPointer();

                for (uint samp = 0; samp < length; samp++)
                {
                    /*
                        Feel free to unroll this.
                    */
                    for (int chan = 0; chan < outchannels; chan++)
                    {
                        /* 
                            This DSP filter just halves the volume! 
                            Input is modified, and sent to output.
                        */
                        outBuff[(samp * outchannels) + chan] = inBuff[(samp * inchannels) + chan];// *0.2f;
                    }
                }
            }
            return FMOD.RESULT.OK;
        }
        #endregion

        #region Playback Methods

        /// <summary>
        /// Updates the FMOD sound system
        /// </summary>
        private void Update()
        {
            _system.update();
        }

        /// <summary>
        /// Begins playback or pauses playback
        /// </summary>
        public void PlayOrPause()
        {
            //if the track is playing, then pressing play again will pause the track
            if (GetIsPlaying())
            {
                //its playing: pause or unpause
                Pause();
            }
            else
            {
                //nothing is playing, play something
                Play();
            }
        }

        /// <summary>
        /// Prepares media for playback
        /// </summary>
        /// <param name="filename"></param>
        public void LoadMedia(PlayListEntry entry)
        {
            FMOD.RESULT result;

            //if we are already playing, we have to stop the current media

            //load a new sound
            result = _system.createSound(entry.FileName, FMOD.MODE._2D | FMOD.MODE.NONBLOCKING | FMOD.MODE.CREATESTREAM, out _sound);
            CheckError(result);

            //how to determine the media is ready? an event perhaps?
            FMOD.OPENSTATE openState;
            uint percentBuffered;
            bool starving;
            bool diskBusy;

            //check to see if it is ready
            _sound.getOpenState(out openState, out percentBuffered, out starving, out diskBusy);

            //check state
            while (openState == FMOD.OPENSTATE.LOADING)
            {
                _sound.getOpenState(out openState, out percentBuffered, out starving, out diskBusy);
                Thread.Sleep(25);
            }

            Length = GetLength();
        }

        /// <summary>
        /// Begins playback. This method is called when the user initiates playback.
        /// </summary>
        private void Play()
        {

            //get some media to play
            var args = new NeedNextMediaEventArgs();

            //fire event
            OnNeedNextMedia(args);

            //check to see if there are
            if (args.Media == null)
            {
                return;
            }




            var playTask = Task.Run(() =>
            {
                //send the media to be played
                LoadMedia(args.Media);
                //play the stream
                PlayStream();
            });

            playTask.ContinueWith((t) =>
            {
                //play the next song
                Play();
            });

            //update the system
            //Task.Run(() =>
            //{

            //    _system.update();

            //});


            ////create a new cancellation token
            //_playbackCts = new CancellationTokenSource();


            ////start a task to prepare the playlist
            //await Task.Run(async () =>
            //{
            //    //clear the internal playlist
            //    InternalPlayList.Clear();

            //    //create the internal playlist
            //    foreach (var item in NowPlaying)
            //    {
            //        //add the item from the now playing window to the internal playlist
            //        InternalPlayList.Add(new MediaInfo() { FileName = item.FileName });
            //    }

            //    //look through the internal playlist
            //    foreach (var media in InternalPlayList)
            //    {
            //        //check is playback is stopped
            //        if (_playbackCts.IsCancellationRequested)
            //        {
            //            break;
            //        }

            //        //create the stream from the media info
            //        await CreateStreamAsync(media);

            //        //play it
            //        var channel = PlayStream(media);



            //        //channel.addDSP(0, dsp);

            //        //now that the sound is playing, we hold the thread hostage.
            //        //a variable controls when the thread should be released.
            //        await Task.Run(() =>
            //        {
            //            FMOD.RESULT result;
            //            bool stopping = false;
            //            IsPlaying = true;

            //            //set control to neutral
            //            PlayControl = PlaybackControl.Neutral;

            //            //get the length of the media
            //            Length = GetLength(media);

            //            //hold thread hostage while the sound is playing. in the meantime,
            //            //update the open state, and update the system so that callbacks will be generated
            //            while (IsPlaying)
            //            {

            //                //if cancellation of the playback token is requested, initiate stop
            //                if (_playbackCts.IsCancellationRequested && !stopping)
            //                {
            //                    //begin stop
            //                    var stopTask = StopStreamAsync(channel, media);
            //                    //prevent from trying to stop again
            //                    stopping = true;
            //                }

            //                //check play control and control playback accordingly
            //                if (PlayControl == PlaybackControl.Pause && !stopping)
            //                {
            //                    //pause or unpause the playback
            //                    Pause(channel);
            //                }
            //                else if (PlayControl == PlaybackControl.Seek)
            //                {
            //                    //seek to new position
            //                    SetPostion(channel, InternalPosition);
            //                }

            //                //update the open state of the media
            //                media.UpdateOpenState();

            //                //is media still playing?
            //                IsPlaying = GetIsPlaying(channel);

            //                //get the position and set it to the internal position property
            //                if (IsPlaying && PlayControl != PlaybackControl.Seek)
            //                {
            //                    InternalPosition = GetPosition(channel);
            //                }

            //                //we must call this once per frame
            //                result = _system.update();
            //                CheckError(result);

            //                //return to resting after executing a play control command
            //                PlayControl = PlaybackControl.Neutral;

            //                //wait
            //                Thread.Sleep(5);
            //            }

            //        }, _playbackCts.Token);

            //        //ensure everything knows that playback has stopped
            //        IsPlaying = false;
            //    }

            //});
        }

        /// <summary>
        /// Takes a media info object and plays it on an open channel
        /// </summary>
        /// <param name="media"></param>
        /// <returns></returns>
        private void PlayStream()
        {
            //result
            FMOD.RESULT result;

            //if (_channel != null)
            //{
            //    //clear callback
            //    //result = _channel.setCallback(null);
            //    //CheckError(result);

            //    //end playback
            //    if (IsPlaying)
            //    {
            //        result = _channel.stop();
            //        CheckError(result);
            //    }
            //}

            //play the sound on the channel
            result = _system.playSound(_sound, null, true, out _channel);
            CheckError(result);
                        
            //create the dsp, although it will not be active at this time
            result = _system.createDSP(ref dspDesc, out _dsp);

            ////deactivate
            //_dsp.setBypass(true);

            //add to dsp chain
            _channel.addDSP(0, _dsp);

            //unpause when ready to begin playing
            result = _channel.setPaused(false);
            CheckError(result);

            ////activate
            //_dsp.setBypass(false);
            
            

            //hold the thread hostage
            while (GetIsPlaying())
            {
                Update();

                InternalPosition = GetPosition();


                Thread.Sleep(25);

            }


        }

        /// <summary>
        /// Pauses or unpauses playback
        /// </summary>
        private void Pause()
        {
            FMOD.RESULT result;
            bool paused;
            result = _channel.getPaused(out paused);
            CheckError(result);

            result = _channel.setPaused(!paused);
            CheckError(result);

        }

        /// <summary>
        /// Stops playback
        /// </summary>
        public async Task StopAsync()
        {
            //stops playback of the media
            _playbackCts.Cancel();

            await Task.Run(() =>
            {
                //wait until IsPlaying is false
                while (IsPlaying)
                {
                    Thread.Sleep(5);
                }

            });
        }

        /// <summary>
        /// Stops the playback of the channel and destroys the channel
        /// </summary>
        private async Task StopStreamAsync(FMOD.Channel channel, MediaInfo media)
        {

            await Task.Run(() =>
            {
                FMOD.RESULT result;
                //fade out
                //TODO replace 1.0 with current volume level
                for (float x = 1.0f; x > 0; x -= 0.05f)
                {
                    //set volume
                    result = channel.setVolume(x);
                    CheckError(result);

                    //sleep
                    Thread.Sleep(25);

                }

                //stop playback
                result = channel.stop();
                CheckError(result);

                //release the sound
                result = media.Sound.release();
                CheckError(result);


            });

        }

        #endregion

        #region Media Query

        //public void UpdateOpenState()
        //{
        //    if (Sound != null)
        //    {
        //        FMOD.OPENSTATE openState;
        //        uint percentBuffered;
        //        bool starving;
        //        bool diskBusy;

        //        //check to see if it is ready
        //        Sound.getOpenState(out openState, out percentBuffered, out starving, out diskBusy);

        //        //set the open state
        //        OpenState = openState;
        //    }
        //}

        /// <summary>
        /// Gets whether the channel is currently playing
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        private bool GetIsPlaying()
        {
            if (this._channel == null) return false;

            FMOD.RESULT result;
            bool isPlaying;

            //get is playing
            result = _channel.isPlaying(out isPlaying);
            //CheckError(result);

            return isPlaying;
        }

        /// <summary>
        /// Gets the length of the media in milliseconds
        /// </summary>
        /// <param name="media"></param>
        /// <returns></returns>
        private uint GetLength()
        {

            FMOD.RESULT result;
            uint length;

            //get length
            result = _sound.getLength(out length, FMOD.TIMEUNIT.MS);
            CheckError(result);

            return length;
        }

        /// <summary>
        /// Gets the position of the media playback in milliseconds
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        private uint GetPosition()
        {
            FMOD.RESULT result;
            uint position;

            //get position
            result = _channel.getPosition(out position, FMOD.TIMEUNIT.MS);
            CheckError(result);

            return position;
        }

        /// <summary>
        /// Sets the position of the playing stream
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="position"></param>
        private void SetPostion(uint position)
        {
            FMOD.RESULT result;

            //set position
            result = _channel.setPosition(position, FMOD.TIMEUNIT.MS);
            CheckError(result);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Checks the result for errors
        /// </summary>
        /// <param name="result"></param>
        private void CheckError(FMOD.RESULT result)
        {
            if (result != FMOD.RESULT.OK)
            {
                //get error string
                var error = FMOD.Error.String(result);
                //throw the error
                throw new Exception(error);
            }
        }
        #endregion

        #region Event Handlers

        /// <summary>
        /// Fires the PropertyChanged event
        /// </summary>
        /// <param name="expression"></param>
        protected void OnPropertyChanged(string propertyName)
        {
            //fire event
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        protected void OnNeedNextMedia(NeedNextMediaEventArgs e)
        {
            //fire event
            var handler = NeedNextMedia;
            if (handler != null)
            {
                handler(this, e);
            }
        }

        #endregion


    }
}
