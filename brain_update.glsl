#[compute]
#version 450

// Description:
// 3D Wirebrain (look up Wireworld cellular automata)
// charge propagates along wires according to activation
// and decay rules.
// state 0 = no charge
// state 9 = activated
// state 1 = refractory period after activation cannot be activated
// (refractory decays back to 0, so 2-8 are reserved for future expansion exploring longer decays)
// activation rule: iff exactly 1 or 2 (of the 26 immediate) neighbors are activated, less no, more no.

// n1 n2 variables:
// on the CPU side I render an animation of where signal is traveling.
// this requires knowing where a current activation has come from (direction in 3D space)
// I store this in the n1 n2 variables as an integer that maps to the unique 26 directions (a lookup table on CPU side)
// the trick is that sometimes there is only 1 neighbor from which current activation came from.
// sometimes there are two neighbors from which current activation came from. 0 means no neighbor information.

// Matrices:
// set up like doing convolution, where boundaries are padded so we
// don't have to worry about boundary conditions.
// so for a 3x3x3 'brain' the matrices will be 5x5x5 with 0s on the edges.

// seems decent - no clue. CPU code assumes this size to calculate
// compute dimensions to ask for.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

// these matrices are all the same size 3D
layout(r8, binding = 0) restrict uniform image3D cm; // location is connection (0 or 1, is a wire that signal can pass through)
layout(r8, binding = 1) restrict uniform image3D amt0; // amount of charge at location
layout(r8, binding = 2) restrict uniform image3D amt1; // amount of new charge at location (used like temporary variable)
layout(r8, binding = 3) restrict uniform image3D n1; // direction of first charged neighbor we got signal from
layout(r8, binding = 4) restrict uniform image3D n2; // direction of second charged neighbor we got signal from
layout(r8, binding = 5) restrict uniform image3D ins; // is an input node (sensorial/afferent connection)
layout(r8, binding = 6) restrict uniform image3D outs; // is an output node (muscle/efferent connection)
layout(binding = 7) uniform Dimensions {
int x,y,z;
} Dim;

// working with integers is slightly annoying!
// make the code more readable with macros:
#define set(img,p,v) {imageStore(img, p, uintBitsToFloat(uvec4(v, 0, 0, 0)));}
#define get(img,p) floatBitsToInt(imageLoad(img, p))
// unused
#define set2(img,p,v1,v2) {imageStore(img, p, uintBitsToFloat(uvec4(v1, v2, 0, 0)));}

// state values for readable code
const int REFRACTORY = 1;
const int ON = 9; // active
const int OFF = 0; // inactive

/// Can't have dynamic for loops! wow
/// leaving this here as poor man notes for future ref.
// layout( push_constant ) uniform constants
// {
	// int W; // actual width (simulated matrix dim0 + 2 for convolutional padding)
// } PC;

void main() {
	for (int repeats=0; repeats < 1; ++repeats) {
    uvec3 upos = uvec3(gl_GlobalInvocationID.xyz);
    ivec3 ipos = ivec3(upos);

	// if boundary condition, then don't compute anything, it's just padding
	if (ipos.x == 0 || ipos.y == 0 || ipos.z == 0 || ipos.x >= Dim.x-1 || ipos.y >= Dim.y-1 || ipos.z >= Dim.z-1)
		return;

	// accumulate charge
	uint neighbor_charge = 0;
	ivec3 offset = ivec3(0,0,0);
	
	uint neighbor1 = 0;
	uint neighbor2 = 0;
	for (int zoffset=0; zoffset<2; ++zoffset) {
		for (int xoffset=0; xoffset<3; ++xoffset) {
			for (int yoffset=0; yoffset<3; ++yoffset){
				offset = ipos + ivec3(xoffset-1,yoffset-1,zoffset*2-1);
				neighbor_charge += get(cm, offset).x * uint(get(amt0, offset).x == ON);
				
				neighbor2 += uint(neighbor1 != 0) * uint(neighbor2 == 0) * get(cm, offset).x * uint(get(amt0, offset).x == ON) * (zoffset*2*3*3 + yoffset*3 + xoffset + 1); // must be first
				neighbor1 += uint(neighbor1 == 0) * get(cm, offset).x * uint(get(amt0, offset).x == ON) * (zoffset*2*3*3 + yoffset*3 + xoffset + 1);
			}
		}
	}
	int zoffset = 0;
	{
		for (int xoffset=0; xoffset<2; ++xoffset) {
			for (int yoffset=0; yoffset<3; ++yoffset){
				offset = ipos + ivec3(xoffset*2-1,yoffset-1,zoffset);
				neighbor_charge += get(cm, offset).x * uint(get(amt0, offset).x == ON);
				
				neighbor2 += uint(neighbor1 != 0) * uint(neighbor2 == 0) * get(cm, offset).x * uint(get(amt0, offset).x == ON) * (3*3 + yoffset*3 + xoffset*2 + 1); // must be first
				neighbor1 += uint(neighbor1 == 0) * get(cm, offset).x * uint(get(amt0, offset).x == ON) * (3*3 + yoffset*3 + xoffset*2 + 1);
			}
		}
	}
	int xoffset = 0;
	for (int yoffset=0; yoffset<2; ++yoffset){
		offset = ipos + ivec3(xoffset,yoffset*2-1,zoffset);
		neighbor_charge += get(cm, offset).x * uint(get(amt0, offset).x == ON);
		
		neighbor2 += uint(neighbor1 != 0) * uint(neighbor2 == 0) * get(cm, offset).x * uint(get(amt0, offset).x == ON) * (3*3 + yoffset*2*3 + 1 + 1); // must be first
		neighbor1 += uint(neighbor1 == 0) * get(cm, offset).x * uint(get(amt0, offset).x == ON) * (3*3 + yoffset*2*3 + 1 + 1);
	}
	
	neighbor_charge -= get(cm, ipos).x * uint(get(amt0, ipos).x == ON); // subtract self

	uint cur_value = get(amt0, ipos).x;
	
	uint new_potential = uint(neighbor_charge >= 1) * // good if >= 1 neighbor
	                     uint(neighbor_charge < 3) * // good if < 3 neighbor
						 uint(cur_value < 1); // good if cur_value == 0 (OFF)

	// branchless state machine
	uint new_value = uint(cur_value >= OFF+1) * uint(cur_value < ON) * uint(cur_value - 1) + // refractory -> refractory-1
					 uint(cur_value >= ON) * REFRACTORY + // on -> refractory
					 uint(cur_value < OFF+1) * ON * new_potential * get(cm, ipos).x; // off -> on
	
	set(n1, ipos, neighbor1);
	set(n2, ipos, neighbor2);
	set(amt1, ipos, new_value); // write t+1
	
	memoryBarrier(); // sync
	
	set(amt0, ipos, get(amt1, ipos).x); // propagate from t+1 to t
	}
}