Silent audio for the audio tests, generated for this project (no third-party content):

- `silence-0.5s.wav`, `silence-3s.wav`: 16 kHz mono PCM, written with Python's `wave` module.
- `silence-1s.mp3`: 44.1 kHz mono, `gst-launch-1.0 audiotestsrc wave=silence num-buffers=43 samplesperbuffer=1024 ! audio/x-raw,rate=44100,channels=1 ! lamemp3enc ! filesink location=silence-1s.mp3`.
- `not-audio.mp3`: a text file with an .mp3 name, for the "corrupt file" tests.
