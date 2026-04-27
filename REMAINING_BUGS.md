# These are the current remaining bugs for the VideoStages project

## general

[] LTX: two stages without an upscaler or ref image add unnecessary LTXVSeparateAVLatent and LTXVConcatAVLatent.
When no upscaler or ref image is defined, a SwarmKSampler should pipe directly to another SwarmKSampler. Exceptions include if Stage A had a LTXVAddGuide before it, so a LTXVCropGuides is still needed

[] use "VAE Decode (Tiled)" only if user chooses SwarmUI's "VAE Tile Size".
If user does not want to use tiled VAE decode, use the "VAE Decode" node instead

[] acestepfun enabled causes "audio0" to be shown in audio source dropdown
The label should be user-friendly, "AceStepFun Audio 0". Should this be set from the AceStepFun extension?

[] acestepfun track has a "Save Audio (MP3)" node
The audio track is to be used in the video. Add a new "Save Audio Track" checkbox to VideoStages. If checkbox is enabled, keep the "Save Audio (MP3)" node. Otherwise, remove it.

[] swarmui text-to-audio incompatible with video
Remove "Swarm Audio" opion from Audio Source. It can't be used, anyway

[] add new "Reuse Audio" checkbox below "Audio Source"
When enabled, and when a video clip has 3 or more stages, the LTXVSeparateAVLatent.audio_latent output of Stage 0 should be used for all subsequent stage's LTXVConcatAVLatent.audio_latent. For example, in a clip with three stages and this checkbox ON, Stage 0's audio_latent will be used by Stage 2, instead of default behavior of Stage 1's audio_latent used by Stage 2. This helps reinforce the original produced audio.

[] LTX: upscaler and upscaler method should be ignored + logged, when not one of the valid LTX-approved upscale models.
Right now using lanczos incorrectly adds the scaffolding for upscaling.

[] changing upscale method does not update JSON, unless upscale value is changed after
Selecting upscale==1.5, then changing upscale method to a valid choice, does not actually update the JSON. The backend receives the previous upscale method choice. I have to change the uspcale value back and forth to see the upscale method choice persist

## text-to-video workflow

[] swarmui video stage shows up.
If user selects a video model for a text-to-video workflow, and enables VideoStages with one clip, the ComfyUI workflow shows two video stages - the core swarmui stage, and the videostages clip 0. Like image-to-video workflows, VideoStages should replace the core video stage.

[] text-to-video ref image should not apply unless user uploads an image.
text-to-video workflows have no base or refiner image stages. Ref images should be ignored unless they are uploads.

## image-to-video workflow

[] reference images should have a default record, pointing to refiner stage
When in an image-to-video workflow, the extension should populate the default clip with a ref image, image source == refiner, frame = 1, stage 0.reference image 0 strength = 1

[] enforce reference image when not defined
the backend should add a ref image to refiner (or base as fallback), frame = 1, strength = 1, when user does not define one
