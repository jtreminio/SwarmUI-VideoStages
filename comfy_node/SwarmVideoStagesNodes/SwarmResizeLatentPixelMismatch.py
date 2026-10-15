"""Select a resized latent only when its decoded dimensions would differ from the target."""

from comfy_api.latest import io


class SwarmResizeLatentPixelMismatch(io.ComfyNode):
    @classmethod
    def define_schema(cls) -> io.Schema:
        return io.Schema(
            node_id="SwarmResizeLatentPixelMismatch",
            display_name="Resize Latent/Pixel Mismatch",
            category="SwarmUI/latent",
            description=(
                "Keep the original latent when its pixel dimensions match the target. "
                "Otherwise evaluate the connected resize branch. The VAE supplies the spatial scale; "
                "checking dimensions does not decode the latent."
            ),
            inputs=[
                io.Latent.Input("samples"),
                io.Vae.Input("vae"),
                io.Int.Input("width", default=512, min=1, max=16384),
                io.Int.Input("height", default=512, min=1, max=16384),
                io.Latent.Input("resized", lazy=True),
            ],
            outputs=[io.Latent.Output(display_name="latent")],
        )

    @staticmethod
    def _matches_target(samples, vae, width, height):
        tensor = samples["samples"]
        if tensor.ndim not in (4, 5):
            raise ValueError("Expected an image or video latent with spatial dimensions.")
        scale = vae.spacial_compression_decode()
        return tensor.shape[-1] * scale == width and tensor.shape[-2] * scale == height

    @classmethod
    def check_lazy_status(cls, samples, vae, width, height, resized=None):
        if resized is None and not cls._matches_target(samples, vae, width, height):
            return ["resized"]
        return []

    @classmethod
    def execute(cls, samples, vae, width, height, resized=None) -> io.NodeOutput:
        if cls._matches_target(samples, vae, width, height):
            return io.NodeOutput(samples)
        if resized is None:
            raise ValueError("The resize branch did not supply a latent.")
        return io.NodeOutput(resized)
