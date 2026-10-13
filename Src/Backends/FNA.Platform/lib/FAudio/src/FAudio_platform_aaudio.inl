/* WPR: FAudio's output on Android through AAudio, instead of SDL's audio device.
 *
 * Included by FAudio_platform_sdl2.c on __ANDROID__ only. SDL is still used for everything
 * else (threads, mutexes, device names); only the output stream changes.
 *
 * Why: SDL2's Android drivers (OpenSL ES, and its AAudio driver, which asks for neither a
 * performance mode nor a buffer size) open an ordinary track with a large buffer that they
 * keep full. FAudio then mixes 100+ ms ahead of what is being heard, and a voice whose game
 * refills it about once a frame runs dry inside that window: FAudio mixes silence and the
 * gap is heard. Measured on the emulator with a WP8 title (Angry Birds Rio) feeding a voice
 * in 33 ms buffers: about one dry spell a second, where Android's own AudioTrack, with the
 * same game, had a few a minute. The WP7 sound effects and songs pay the same lead as
 * latency (~180 ms on the emulator).
 *
 * This opens a low-latency AAudio stream in callback mode and keeps its buffer at a couple
 * of bursts, growing it one burst at a time if the device reports underruns. The callback
 * mixes one FAudio quantum at a time into a small FIFO and hands out whatever AAudio asks
 * for, so the lead is one quantum plus that buffer.
 *
 * AAudio is API 26+ and this library's minimum is 21, so libaaudio is opened at run time;
 * below 27 (8.0 has a reference-counting crash, which SDL also avoids), or if anything
 * fails, the SDL device is used exactly as before. The SDL hint FAUDIO_ANDROID_OUTPUT=sdl
 * forces the old path.
 *
 * SDL paused its own device when the app went to the background. Nothing does that for
 * this stream, so an SDL event watch pauses and restarts every open stream on the app's
 * background/foreground events. A disconnected stream (headphones unplugged, a Bluetooth
 * device going away) is reopened on a thread of its own, as AAudio requires.
 * -WPR
 */

#include <dlfcn.h>
#include <pthread.h>
#include <aaudio/AAudio.h>

#define WPR_AAUDIO_MAGIC 0x57505241 /* 'WPRA' */
#define WPR_AAUDIO_MAX_DEVICES 8

typedef aaudio_result_t (*PFN_createStreamBuilder)(AAudioStreamBuilder **);
typedef void (*PFN_builder_setInt)(AAudioStreamBuilder *, int32_t);
typedef void (*PFN_builder_setDataCallback)(AAudioStreamBuilder *, AAudioStream_dataCallback, void *);
typedef void (*PFN_builder_setErrorCallback)(AAudioStreamBuilder *, AAudioStream_errorCallback, void *);
typedef aaudio_result_t (*PFN_builder_openStream)(AAudioStreamBuilder *, AAudioStream **);
typedef aaudio_result_t (*PFN_builder_delete)(AAudioStreamBuilder *);
typedef aaudio_result_t (*PFN_stream_op)(AAudioStream *);
typedef int32_t (*PFN_stream_getInt)(AAudioStream *);
typedef aaudio_result_t (*PFN_stream_setBufferSize)(AAudioStream *, int32_t);
typedef aaudio_result_t (*PFN_stream_waitForStateChange)(AAudioStream *, aaudio_stream_state_t, aaudio_stream_state_t *, int64_t);

static struct
{
	int loaded; /* 0 = not tried, 1 = usable, -1 = unusable */
	PFN_createStreamBuilder createStreamBuilder;
	PFN_builder_setInt setDirection;
	PFN_builder_setInt setSharingMode;
	PFN_builder_setInt setPerformanceMode;
	PFN_builder_setInt setFormat;
	PFN_builder_setInt setChannelCount;
	PFN_builder_setInt setSampleRate;
	PFN_builder_setInt setUsage;
	PFN_builder_setInt setContentType;
	PFN_builder_setDataCallback setDataCallback;
	PFN_builder_setErrorCallback setErrorCallback;
	PFN_builder_openStream openStream;
	PFN_builder_delete deleteBuilder;
	PFN_stream_op requestStart;
	PFN_stream_op requestPause;
	PFN_stream_op requestStop;
	PFN_stream_op close;
	PFN_stream_getInt getFramesPerBurst;
	PFN_stream_getInt getBufferCapacityInFrames;
	PFN_stream_getInt getBufferSizeInFrames;
	PFN_stream_getInt getXRunCount;
	PFN_stream_getInt getSampleRate;
	PFN_stream_getInt getChannelCount;
	PFN_stream_getInt getPerformanceMode;
	PFN_stream_setBufferSize setBufferSizeInFrames;
	PFN_stream_waitForStateChange waitForStateChange;
} WPR_AA;

typedef struct WPR_AAudioDevice
{
	uint32_t magic;
	FAudio *audio;
	AAudioStream *stream;
	pthread_mutex_t lock; /* guards stream against reopen/pause/quit */
	int32_t channels;
	int32_t sampleRate;
	uint32_t updateSize;
	float *fifo;
	uint32_t fifoPos;
	uint32_t fifoFrames;
	int32_t burst;
	int32_t lastXRuns;
	int closing;
	int backgrounded;
	int reopening;
} WPR_AAudioDevice;

static WPR_AAudioDevice *WPR_AA_devices[WPR_AAUDIO_MAX_DEVICES];
static SDL_mutex *WPR_AA_listLock = NULL;
static int WPR_AA_watchAdded = 0;

#define WPR_AA_LOG(...) SDL_Log("FAudio: " __VA_ARGS__)

static int WPR_AA_Load(void)
{
	void *lib;
	if (WPR_AA.loaded != 0)
	{
		return WPR_AA.loaded > 0;
	}
	WPR_AA.loaded = -1;

	if (SDL_GetAndroidSDKVersion() < 27)
	{
		return 0;
	}
	lib = dlopen("libaaudio.so", RTLD_NOW);
	if (lib == NULL)
	{
		return 0;
	}

	#define WPR_AA_SYM(field, type, name) \
		WPR_AA.field = (type) dlsym(lib, name); \
		if (WPR_AA.field == NULL) { WPR_AA_LOG("AAudio: missing %s", name); return 0; }
	WPR_AA_SYM(createStreamBuilder, PFN_createStreamBuilder, "AAudio_createStreamBuilder")
	WPR_AA_SYM(setDirection, PFN_builder_setInt, "AAudioStreamBuilder_setDirection")
	WPR_AA_SYM(setSharingMode, PFN_builder_setInt, "AAudioStreamBuilder_setSharingMode")
	WPR_AA_SYM(setPerformanceMode, PFN_builder_setInt, "AAudioStreamBuilder_setPerformanceMode")
	WPR_AA_SYM(setFormat, PFN_builder_setInt, "AAudioStreamBuilder_setFormat")
	WPR_AA_SYM(setChannelCount, PFN_builder_setInt, "AAudioStreamBuilder_setChannelCount")
	WPR_AA_SYM(setSampleRate, PFN_builder_setInt, "AAudioStreamBuilder_setSampleRate")
	WPR_AA_SYM(setDataCallback, PFN_builder_setDataCallback, "AAudioStreamBuilder_setDataCallback")
	WPR_AA_SYM(setErrorCallback, PFN_builder_setErrorCallback, "AAudioStreamBuilder_setErrorCallback")
	WPR_AA_SYM(openStream, PFN_builder_openStream, "AAudioStreamBuilder_openStream")
	WPR_AA_SYM(deleteBuilder, PFN_builder_delete, "AAudioStreamBuilder_delete")
	WPR_AA_SYM(requestStart, PFN_stream_op, "AAudioStream_requestStart")
	WPR_AA_SYM(requestPause, PFN_stream_op, "AAudioStream_requestPause")
	WPR_AA_SYM(requestStop, PFN_stream_op, "AAudioStream_requestStop")
	WPR_AA_SYM(close, PFN_stream_op, "AAudioStream_close")
	WPR_AA_SYM(getFramesPerBurst, PFN_stream_getInt, "AAudioStream_getFramesPerBurst")
	WPR_AA_SYM(getBufferCapacityInFrames, PFN_stream_getInt, "AAudioStream_getBufferCapacityInFrames")
	WPR_AA_SYM(getBufferSizeInFrames, PFN_stream_getInt, "AAudioStream_getBufferSizeInFrames")
	WPR_AA_SYM(getXRunCount, PFN_stream_getInt, "AAudioStream_getXRunCount")
	WPR_AA_SYM(getSampleRate, PFN_stream_getInt, "AAudioStream_getSampleRate")
	WPR_AA_SYM(getChannelCount, PFN_stream_getInt, "AAudioStream_getChannelCount")
	WPR_AA_SYM(getPerformanceMode, PFN_stream_getInt, "AAudioStream_getPerformanceMode")
	WPR_AA_SYM(setBufferSizeInFrames, PFN_stream_setBufferSize, "AAudioStream_setBufferSizeInFrames")
	WPR_AA_SYM(waitForStateChange, PFN_stream_waitForStateChange, "AAudioStream_waitForStateChange")
	#undef WPR_AA_SYM

	/* Usage and content type are API 28; optional. */
	WPR_AA.setUsage = (PFN_builder_setInt) dlsym(lib, "AAudioStreamBuilder_setUsage");
	WPR_AA.setContentType = (PFN_builder_setInt) dlsym(lib, "AAudioStreamBuilder_setContentType");

	WPR_AA.loaded = 1;
	return 1;
}

/* Mixer thread: AAudio's callback. */
static aaudio_data_callback_result_t WPR_AA_Data(
	AAudioStream *stream,
	void *userData,
	void *audioData,
	int32_t numFrames
) {
	WPR_AAudioDevice *dev = (WPR_AAudioDevice*) userData;
	float *out = (float*) audioData;
	const int32_t ch = dev->channels;
	int32_t xruns;

	while (numFrames > 0)
	{
		uint32_t n;
		if (dev->fifoFrames == 0)
		{
			FAudio_zero(dev->fifo, dev->updateSize * ch * sizeof(float));
			if (dev->audio->active)
			{
				FAudio_INTERNAL_UpdateEngine(dev->audio, dev->fifo);
			}
			dev->fifoPos = 0;
			dev->fifoFrames = dev->updateSize;
		}
		n = (uint32_t) numFrames < dev->fifoFrames ? (uint32_t) numFrames : dev->fifoFrames;
		FAudio_memcpy(out, dev->fifo + (dev->fifoPos * ch), n * ch * sizeof(float));
		out += n * ch;
		dev->fifoPos += n;
		dev->fifoFrames -= n;
		numFrames -= (int32_t) n;
	}

	/* Latency tuning, as Oboe does it: start at two bursts, add one whenever the device ran
	 * dry since the last callback, up to the stream's capacity.
	 */
	xruns = WPR_AA.getXRunCount(stream);
	if (xruns > dev->lastXRuns)
	{
		int32_t size = WPR_AA.getBufferSizeInFrames(stream);
		int32_t cap = WPR_AA.getBufferCapacityInFrames(stream);
		dev->lastXRuns = xruns;
		if (size + dev->burst <= cap)
		{
			WPR_AA.setBufferSizeInFrames(stream, size + dev->burst);
		}
	}
	return AAUDIO_CALLBACK_RESULT_CONTINUE;
}

static void WPR_AA_Error(AAudioStream *stream, void *userData, aaudio_result_t error);

/* Opens and starts dev's stream. Caller holds dev->lock. */
static int WPR_AA_Open(WPR_AAudioDevice *dev)
{
	AAudioStreamBuilder *builder;
	AAudioStream *stream = NULL;
	aaudio_result_t res;

	if (WPR_AA.createStreamBuilder(&builder) != AAUDIO_OK)
	{
		return 0;
	}
	WPR_AA.setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
	WPR_AA.setSharingMode(builder, AAUDIO_SHARING_MODE_SHARED);
	WPR_AA.setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);
	WPR_AA.setFormat(builder, AAUDIO_FORMAT_PCM_FLOAT);
	WPR_AA.setChannelCount(builder, dev->channels);
	WPR_AA.setSampleRate(builder, dev->sampleRate);
	if (WPR_AA.setUsage != NULL)
	{
		WPR_AA.setUsage(builder, AAUDIO_USAGE_GAME);
	}
	if (WPR_AA.setContentType != NULL)
	{
		WPR_AA.setContentType(builder, AAUDIO_CONTENT_TYPE_MUSIC);
	}
	WPR_AA.setDataCallback(builder, WPR_AA_Data, dev);
	WPR_AA.setErrorCallback(builder, WPR_AA_Error, dev);
	res = WPR_AA.openStream(builder, &stream);
	WPR_AA.deleteBuilder(builder);
	if (res != AAUDIO_OK || stream == NULL)
	{
		WPR_AA_LOG("AAudio: openStream failed (%d)", res);
		return 0;
	}

	/* The engine was set up for these; anything else and the data would be misread. */
	if (	WPR_AA.getSampleRate(stream) != dev->sampleRate ||
		WPR_AA.getChannelCount(stream) != dev->channels	)
	{
		WPR_AA_LOG(
			"AAudio: stream is %d Hz x%d, wanted %d Hz x%d",
			WPR_AA.getSampleRate(stream), WPR_AA.getChannelCount(stream),
			dev->sampleRate, dev->channels
		);
		WPR_AA.close(stream);
		return 0;
	}

	dev->burst = WPR_AA.getFramesPerBurst(stream);
	if (dev->burst <= 0)
	{
		dev->burst = 192;
	}
	{
		const char *bursts = SDL_GetHint("FAUDIO_ANDROID_BUFFER_BURSTS");
		int32_t count = bursts != NULL ? SDL_atoi(bursts) : 2;
		if (count < 1)
		{
			count = 2;
		}
		WPR_AA.setBufferSizeInFrames(stream, dev->burst * count);
	}
	dev->lastXRuns = WPR_AA.getXRunCount(stream);
	dev->fifoFrames = 0;
	dev->stream = stream;

	if (!dev->backgrounded && WPR_AA.requestStart(stream) != AAUDIO_OK)
	{
		WPR_AA_LOG("AAudio: requestStart failed");
		WPR_AA.close(stream);
		dev->stream = NULL;
		return 0;
	}

	WPR_AA_LOG(
		"AAudio output: %d Hz x%d, quantum %u, burst %d, buffer %d of %d, %s",
		dev->sampleRate, dev->channels, dev->updateSize, dev->burst,
		WPR_AA.getBufferSizeInFrames(stream),
		WPR_AA.getBufferCapacityInFrames(stream),
		WPR_AA.getPerformanceMode(stream) == AAUDIO_PERFORMANCE_MODE_LOW_LATENCY ?
			"low latency" : "not low latency"
	);
	return 1;
}

/* Stops and closes dev's stream. Caller holds dev->lock. */
static void WPR_AA_Close(WPR_AAudioDevice *dev)
{
	if (dev->stream != NULL)
	{
		WPR_AA.requestStop(dev->stream);
		WPR_AA.close(dev->stream); /* waits for a callback in progress */
		dev->stream = NULL;
	}
}

static void *WPR_AA_Reopen(void *arg)
{
	WPR_AAudioDevice *dev = (WPR_AAudioDevice*) arg;
	pthread_mutex_lock(&dev->lock);
	if (!dev->closing)
	{
		WPR_AA_Close(dev);
		if (!WPR_AA_Open(dev))
		{
			WPR_AA_LOG("AAudio: could not reopen the output after a disconnect");
		}
	}
	pthread_mutex_unlock(&dev->lock);
	__atomic_store_n(&dev->reopening, 0, __ATOMIC_RELEASE); /* last touch: Quit may free dev after this */
	return NULL;
}

/* AAudio forbids closing a stream from its own error callback, hence the thread. */
static void WPR_AA_Error(AAudioStream *stream, void *userData, aaudio_result_t error)
{
	WPR_AAudioDevice *dev = (WPR_AAudioDevice*) userData;
	pthread_t thread;
	WPR_AA_LOG("AAudio: stream error %d, reopening", error);
	if (dev->closing || dev->reopening)
	{
		return;
	}
	dev->reopening = 1;
	if (pthread_create(&thread, NULL, WPR_AA_Reopen, dev) == 0)
	{
		pthread_detach(thread);
	}
	else
	{
		dev->reopening = 0;
	}
}

/* SDL raises these from the activity's onPause/onResume. */
static int WPR_AA_EventWatch(void *userdata, SDL_Event *event)
{
	int i, background;
	if (event->type == SDL_APP_DIDENTERBACKGROUND)
	{
		background = 1;
	}
	else if (event->type == SDL_APP_WILLENTERFOREGROUND)
	{
		background = 0;
	}
	else
	{
		return 0;
	}

	SDL_LockMutex(WPR_AA_listLock);
	for (i = 0; i < WPR_AAUDIO_MAX_DEVICES; i += 1)
	{
		WPR_AAudioDevice *dev = WPR_AA_devices[i];
		if (dev == NULL)
		{
			continue;
		}
		pthread_mutex_lock(&dev->lock);
		dev->backgrounded = background;
		if (dev->stream != NULL)
		{
			if (background)
			{
				WPR_AA.requestPause(dev->stream);
			}
			else
			{
				WPR_AA.requestStart(dev->stream);
			}
		}
		pthread_mutex_unlock(&dev->lock);
	}
	SDL_UnlockMutex(WPR_AA_listLock);
	return 0;
}

static int WPR_AA_IsDevice(void *platformDevice)
{
	return	(size_t) platformDevice > 0xFFFF &&
		((WPR_AAudioDevice*) platformDevice)->magic == WPR_AAUDIO_MAGIC;
}

/* Returns 1 and fills the outputs if AAudio carries this engine; 0 to use SDL. */
static int WPR_AA_TryInit(
	FAudio *audio,
	uint32_t flags,
	uint32_t deviceIndex,
	FAudioWaveFormatExtensible *mixFormat,
	uint32_t *updateSize,
	void** platformDevice
) {
	WPR_AAudioDevice *dev;
	const char *hint = SDL_GetHint("FAUDIO_ANDROID_OUTPUT");
	int i, slot = -1;

	if (hint != NULL && SDL_strcasecmp(hint, "sdl") == 0)
	{
		return 0;
	}
	if (deviceIndex > 1 || !WPR_AA_Load())
	{
		return 0; /* AAudio has only the default output */
	}

	if (WPR_AA_listLock == NULL)
	{
		WPR_AA_listLock = SDL_CreateMutex();
	}
	SDL_LockMutex(WPR_AA_listLock);
	for (i = 0; i < WPR_AAUDIO_MAX_DEVICES; i += 1)
	{
		if (WPR_AA_devices[i] == NULL)
		{
			slot = i;
			break;
		}
	}
	SDL_UnlockMutex(WPR_AA_listLock);
	if (slot < 0)
	{
		return 0;
	}

	dev = (WPR_AAudioDevice*) SDL_calloc(1, sizeof(WPR_AAudioDevice));
	dev->magic = WPR_AAUDIO_MAGIC;
	dev->audio = audio;
	dev->channels = mixFormat->Format.nChannels;
	dev->sampleRate = mixFormat->Format.nSamplesPerSec;
	if (flags & FAUDIO_1024_QUANTUM)
	{
		dev->updateSize = (uint32_t) (dev->sampleRate / (1000.0 / (64.0 / 3.0)));
	}
	else
	{
		dev->updateSize = dev->sampleRate / 100;
	}
	dev->fifo = (float*) SDL_malloc(dev->updateSize * dev->channels * sizeof(float));
	pthread_mutex_init(&dev->lock, NULL);

	/* The engine reads these before the first callback can run. */
	WriteWaveFormatExtensible(
		mixFormat,
		dev->channels,
		dev->sampleRate,
		&DATAFORMAT_SUBTYPE_IEEE_FLOAT
	);
	*updateSize = dev->updateSize;

	pthread_mutex_lock(&dev->lock);
	if (!WPR_AA_Open(dev))
	{
		pthread_mutex_unlock(&dev->lock);
		pthread_mutex_destroy(&dev->lock);
		SDL_free(dev->fifo);
		SDL_free(dev);
		return 0;
	}
	pthread_mutex_unlock(&dev->lock);

	SDL_LockMutex(WPR_AA_listLock);
	WPR_AA_devices[slot] = dev;
	if (!WPR_AA_watchAdded)
	{
		WPR_AA_watchAdded = 1;
		SDL_AddEventWatch(WPR_AA_EventWatch, NULL);
	}
	SDL_UnlockMutex(WPR_AA_listLock);

	*platformDevice = dev;
	return 1;
}

static void WPR_AA_Quit(WPR_AAudioDevice *dev)
{
	int i;
	SDL_LockMutex(WPR_AA_listLock);
	for (i = 0; i < WPR_AAUDIO_MAX_DEVICES; i += 1)
	{
		if (WPR_AA_devices[i] == dev)
		{
			WPR_AA_devices[i] = NULL;
		}
	}
	SDL_UnlockMutex(WPR_AA_listLock);

	pthread_mutex_lock(&dev->lock);
	dev->closing = 1;
	WPR_AA_Close(dev);
	pthread_mutex_unlock(&dev->lock);

	/* A reopen thread may still be waiting for the lock; it sees closing and leaves. */
	while (__atomic_load_n(&dev->reopening, __ATOMIC_ACQUIRE))
	{
		SDL_Delay(1);
	}
	pthread_mutex_destroy(&dev->lock);
	dev->magic = 0;
	SDL_free(dev->fifo);
	SDL_free(dev);
}
