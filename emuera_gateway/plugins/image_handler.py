"""Placeholder for future image processing logic."""
from ..plugin import Plugin


class ImageHandlerPlugin(Plugin):
    """Placeholder for future image processing logic."""

    name = "image_handler"

    def on_turn(self, turn):
        # TODO: intercept image generation commands, offload to external service
        return turn

    def on_input(self, value):
        return value
