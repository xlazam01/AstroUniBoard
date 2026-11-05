Set Rotator Position: Send a standard LX200 movement command to position the axis to a specific angle (e.g., 0 to 360 degrees).Return Home: Sends the rotator back to its designated 0-degree start position.Set Home: Manually sets the current physical location as the 0-degree position.De-rotation Control: For Alt/Az mounts, you can use the software interface to move to the parallactic angle and enable Alt/Az field de-rotation.

Test step expeceations
1. check right angle range and orientation
- range 0-360degree is reported - but due to cable untangle feature is supposedly be internly handled as +-180 from 0 as base position
- 0/360degree - base/default position is the possiotion where rotator and camera image is in landscape mode with correct orientation
- 90deg means rotator turns counterclocwise - picture is portraited
-180deg is flipped horizontaly
-270deg is portrait, rotated clockwise from base/default position
- rotator is capable of max one turn, but is shall go +-180degree from base posisiton to stay i safety margins - ie. when at 180deg and is commanded to go 270, it will go through zero - so cables do not get tangled
2. set current position as home
3. Homing precision
- rotator goes to 10deg
- and try to search home again
4. Find gravitational 0/base position
- after this command rotator shall find gravitional zero 
5. Homin shall follow same algorithm - untangling cables - ie from 270deg not the shortest path but unwind the cables
- rotator goes to 90 and back home
- rotator goes to 270 and back home

