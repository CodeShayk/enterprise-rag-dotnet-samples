---
id: VFD-MAN-F012
title: VD-75 variable-frequency drive: fault F012
tenant: motion
groups: motion, service
---

# Fault F012: output overcurrent

Fault F012 means the drive's output current exceeded 200 percent of its rating. The drive stops the motor at once to protect its output stage.

Check for a jammed load, such as a blocked conveyor or a seized gearbox, and for a short circuit in the motor cable. A ramp-up time that is too short also causes F012: raise parameter 2-10 (ramp-up time) from 2 to 5 seconds. Reset F012 with the STOP/RESET key once the cause is removed.
