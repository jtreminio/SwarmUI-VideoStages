"""ComfyUI node: HDR10 sibling of core's SwarmSaveAnimationWS.

Copied from SwarmUI core's SwarmSaveAnimationWS and specialized for HDR: the
input is linear Rec.709 HDR light (the HDR IC-LoRA `hdr_linear` output), which is
PQ-encoded to Rec.2020 and written as a 10-bit HDR10 mp4 (HEVC). Kept legacy-style
so it mirrors the core node's websocket delivery protocol exactly (type_num 5 =
mp4, which SwarmUI decodes as VideoMp4).
"""

import io, struct, subprocess, os, random, sys, time, wave
import folder_paths
import numpy as np
from server import PromptServer, BinaryEventTypes
from imageio_ffmpeg import get_ffmpeg_exe

from .hdr_pq import linear709_to_pq2020

VIDEO_ID = 12346
FFMPEG_PATH = get_ffmpeg_exe()


class SwarmSaveHDRAnimationWS:
    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "images": ("IMAGE", ),
                "fps": ("FLOAT", {"default": 6.0, "min": 0.01, "max": 1000.0, "step": 0.01}),
                "diffuse_white_nits": ("FLOAT", {"default": 100.0, "min": 1.0, "max": 10000.0, "step": 1.0}),
            },
            "optional": {
                "audio": ("AUDIO", )
            }
        }

    CATEGORY = "SwarmUI/video"
    RETURN_TYPES = ()
    FUNCTION = "save_images"
    OUTPUT_NODE = True

    def save_images(self, images, fps, diffuse_white_nits, audio=None):
        if images.shape[0] == 0:
            return { }

        # Linear Rec.709 HDR -> PQ Rec.2020 (0..1), carried to ffmpeg at 16-bit precision.
        pq = linear709_to_pq2020(images, float(diffuse_white_nits))
        i = 65535. * pq.cpu().numpy()
        raw_images = np.clip(i, 0, 65535).astype(np.uint16)
        args = [FFMPEG_PATH, "-v", "error", "-f", "rawvideo", "-pix_fmt", "rgb48le",
                "-s", f"{len(raw_images[0][0])}x{len(raw_images[0])}", "-r", str(fps), "-i", "-", "-n" ]
        # Input is already PQ-encoded Rec.2020; carry it faithfully to 10-bit HEVC and tag HDR10.
        video_args = ["-c:v", "libx265", "-pix_fmt", "yuv420p10le", "-crf", "18",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc",
            "-x265-params", "hdr10=1:hdr10-opt=1:repeat-headers=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(8500,39850)B(6550,2300)R(35400,14600)WP(15635,16450)L(10000000,1):max-cll=1000,400"]
        audio_args = ["-c:a", "aac"]
        ext = "mp4"
        type_num = 5
        path = folder_paths.get_save_image_path("swarm_tmp_", folder_paths.get_temp_directory())[0]
        rand = '%016x' % random.getrandbits(64)
        file = os.path.join(path, f"swarm_tmp_{rand}.{ext}")
        file_2 = None
        audio_input = []
        if audio is not None:
            waveform = audio['waveform']
            if waveform.dim() == 3:
                waveform = waveform[0]
            sample_rate = audio['sample_rate']
            channels = waveform.shape[0]
            num_audio_samples = waveform.shape[1]
            video_duration = len(raw_images) / fps
            target_samples = int(video_duration * sample_rate)
            audio_np = waveform.cpu().numpy()
            if num_audio_samples > target_samples:
                audio_np = audio_np[:, :target_samples]
            elif num_audio_samples < target_samples:
                padding = np.zeros((channels, target_samples - num_audio_samples), dtype=audio_np.dtype)
                audio_np = np.concatenate([audio_np, padding], axis=1)
            audio_np = audio_np.T
            audio_int16 = (np.clip(audio_np, -1.0, 1.0) * 32767).astype(np.int16)
            file_2 = os.path.join(path, f"swarm_tmp_{rand}_audio.wav")
            with wave.open(file_2, 'wb') as wav_file:
                wav_file.setnchannels(channels)
                wav_file.setsampwidth(2)
                wav_file.setframerate(sample_rate)
                wav_file.writeframes(audio_int16.tobytes())
            audio_input = ["-i", file_2]
        else:
            audio_args = []
        result = subprocess.run(args + audio_input + video_args + audio_args + [file], input=raw_images.tobytes(), capture_output=True)
        if result.returncode != 0:
            print(f"ffmpeg failed with return code {result.returncode}", file=sys.stderr)
            f_out = result.stdout.decode("utf-8").strip()
            f_err = result.stderr.decode("utf-8").strip()
            if f_out:
                print("ffmpeg out: " + f_out, file=sys.stderr)
            if f_err:
                print("ffmpeg error: " + f_err, file=sys.stderr)
            raise Exception(f"ffmpeg failed: {f_err}")
        out_img = io.BytesIO()
        with open(file, "rb") as f:
            out_img.write(f.read())
        os.remove(file)
        if file_2 is not None:
            os.remove(file_2)

        out = io.BytesIO()
        header = struct.pack(">I", type_num)
        out.write(header)
        out.write(out_img.getvalue())
        out.seek(0)
        preview_bytes = out.getvalue()
        server = PromptServer.instance
        server.send_sync("progress", {"value": VIDEO_ID, "max": VIDEO_ID}, sid=server.client_id)
        server.send_sync(BinaryEventTypes.PREVIEW_IMAGE, preview_bytes, sid=server.client_id)

        return { }

    @classmethod
    def IS_CHANGED(s, images, fps, diffuse_white_nits, audio=None):
        return time.time()


NODE_CLASS_MAPPINGS = {
    "SwarmSaveHDRAnimationWS": SwarmSaveHDRAnimationWS,
}
