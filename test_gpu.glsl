#[compute]
#version 450

// Description:
// Test the GPU by multiplying the input matrix
// by itself, element-wise.

// Example:
// input [
//        [[1,1],
//         [2,2]],
//         [[3,3],
//		   [4,4]]
//		 ]
// output [
//        [[1,1],
//         [4,4]],
//         [[9,9],
//		   [16,16]]
//		 ]

layout(local_size_x = 2, local_size_y = 2, local_size_z = 2) in;

// these matrices are all the same size 3D
layout(r8, binding = 0) restrict uniform image3D mat; // location is connection

// working with integers is slightly annoying!
// make the code more readable with macros:
#define set(img,p,v) {imageStore(img, p, uintBitsToFloat(uvec4(v, 0, 0, 0)));}
#define get(img,p) floatBitsToInt(imageLoad(img, p))

void main() {
	// get this thread's position in the matrix
    uvec3 upos = uvec3(gl_GlobalInvocationID.xyz);
    ivec3 ipos = ivec3(upos);

	uint old_value = get(mat, ipos).x;
	
	uint new_value = old_value * old_value; // y = x*x;
	
	memoryBarrier(); // sync
	
	set(mat, ipos, new_value); // x = y;
}