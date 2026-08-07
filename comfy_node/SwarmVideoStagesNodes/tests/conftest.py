"""Make the ``SwarmVideoStagesNodes`` package importable from a repo checkout."""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
