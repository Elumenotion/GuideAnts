"""GuideAnts InfiniteTalk video adapter."""

from .app import APP, create_app
from .core import AdapterService, WORKFLOW_VERSION

__all__ = ["APP", "AdapterService", "WORKFLOW_VERSION", "create_app"]
