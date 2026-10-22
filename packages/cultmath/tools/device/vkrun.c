// Vulkan compute host for the conforming run: one dispatch of a generated shader on the first Vulkan device,
// binding 0 = input words, binding 1 = output words. It prints the device's name on stdout and judges nothing.
// usage: vkrun <spv> <in.bin> <out.bin> <ncases> <out_words_per_case>
#include <vulkan/vulkan.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define CK(x) do { VkResult r_ = (x); if (r_ != VK_SUCCESS) { fprintf(stderr, "FAIL %s = %d\n", #x, r_); exit(2); } } while (0)

static void *slurp(const char *p, size_t *n) {
  FILE *f = fopen(p, "rb"); if (!f) { perror(p); exit(2); }
  fseek(f, 0, SEEK_END); *n = ftell(f); fseek(f, 0, SEEK_SET);
  void *b = malloc(*n ? *n : 4); if (fread(b, 1, *n, f) != *n) exit(2); fclose(f); return b;
}

int main(int argc, char **argv) {
  if (argc != 6) { fprintf(stderr, "usage\n"); return 2; }
  size_t spvn, inn; void *spv = slurp(argv[1], &spvn); void *in = slurp(argv[2], &inn);
  unsigned ncases = atoi(argv[4]), ow = atoi(argv[5]); const char *entry = "main";
  size_t outn = (size_t)ncases * ow * 4;

  VkApplicationInfo ai = { VK_STRUCTURE_TYPE_APPLICATION_INFO, 0, "vkrun", 1, "vkrun", 1, VK_API_VERSION_1_2 };
  VkInstanceCreateInfo ici = { VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO, 0, 0, &ai };
  VkInstance inst; CK(vkCreateInstance(&ici, 0, &inst));
  uint32_t np = 1; VkPhysicalDevice pd; vkEnumeratePhysicalDevices(inst, &np, &pd); if (!np) { fprintf(stderr, "no device\n"); return 2; }
  VkPhysicalDeviceProperties props;
  vkGetPhysicalDeviceProperties(pd, &props);
  printf("device=%s\n", props.deviceName);

  float prio = 1; VkDeviceQueueCreateInfo qci = { VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO, 0, 0, 0, 1, &prio };
  VkDeviceCreateInfo dci = { VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO, 0, 0, 1, &qci };
  VkDevice dev; CK(vkCreateDevice(pd, &dci, 0, &dev)); VkQueue q; vkGetDeviceQueue(dev, 0, 0, &q);

  VkPhysicalDeviceMemoryProperties mp; vkGetPhysicalDeviceMemoryProperties(pd, &mp);
  VkBuffer buf[2]; VkDeviceMemory mem[2]; size_t sz[2] = { inn, outn }; void *map[2];
  for (int i = 0; i < 2; i++) {
    VkBufferCreateInfo bci = { VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, 0, 0, sz[i], VK_BUFFER_USAGE_STORAGE_BUFFER_BIT };
    CK(vkCreateBuffer(dev, &bci, 0, &buf[i])); VkMemoryRequirements mr; vkGetBufferMemoryRequirements(dev, buf[i], &mr);
    uint32_t t = 0; VkMemoryPropertyFlags want = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
    for (; t < mp.memoryTypeCount; t++) if ((mr.memoryTypeBits & (1u << t)) && (mp.memoryTypes[t].propertyFlags & want) == want) break;
    VkMemoryAllocateInfo mai = { VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, 0, mr.size, t };
    CK(vkAllocateMemory(dev, &mai, 0, &mem[i])); CK(vkBindBufferMemory(dev, buf[i], mem[i], 0));
    CK(vkMapMemory(dev, mem[i], 0, sz[i], 0, &map[i]));
  }
  memcpy(map[0], in, inn); memset(map[1], 0xEE, outn);

  VkDescriptorSetLayoutBinding b[2] = { { 0, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 1, VK_SHADER_STAGE_COMPUTE_BIT },
                                        { 1, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 1, VK_SHADER_STAGE_COMPUTE_BIT } };
  VkDescriptorSetLayoutCreateInfo lci = { VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO, 0, 0, 2, b };
  VkDescriptorSetLayout dsl; CK(vkCreateDescriptorSetLayout(dev, &lci, 0, &dsl));
  VkPipelineLayoutCreateInfo plci = { VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO, 0, 0, 1, &dsl };
  VkPipelineLayout pl; CK(vkCreatePipelineLayout(dev, &plci, 0, &pl));
  VkShaderModuleCreateInfo smci = { VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO, 0, 0, spvn, spv };
  VkShaderModule sm; CK(vkCreateShaderModule(dev, &smci, 0, &sm));
  VkComputePipelineCreateInfo cpci = { VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO, 0, 0,
    { VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, 0, 0, VK_SHADER_STAGE_COMPUTE_BIT, sm, entry }, pl };
  VkPipeline pipe; CK(vkCreateComputePipelines(dev, 0, 1, &cpci, 0, &pipe));

  VkDescriptorPoolSize ps = { VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 2 };
  VkDescriptorPoolCreateInfo dpci = { VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO, 0, 0, 1, 1, &ps };
  VkDescriptorPool dp; CK(vkCreateDescriptorPool(dev, &dpci, 0, &dp));
  VkDescriptorSetAllocateInfo dsai = { VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO, 0, dp, 1, &dsl };
  VkDescriptorSet ds; CK(vkAllocateDescriptorSets(dev, &dsai, &ds));
  VkDescriptorBufferInfo dbi[2] = { { buf[0], 0, VK_WHOLE_SIZE }, { buf[1], 0, VK_WHOLE_SIZE } };
  VkWriteDescriptorSet w[2];
  for (int i = 0; i < 2; i++) { VkWriteDescriptorSet x = { VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, 0, ds, i, 0, 1, VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 0, &dbi[i] }; w[i] = x; }
  vkUpdateDescriptorSets(dev, 2, w, 0, 0);

  VkCommandPoolCreateInfo cpi = { VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO, 0, 0, 0 };
  VkCommandPool cp; CK(vkCreateCommandPool(dev, &cpi, 0, &cp));
  VkCommandBufferAllocateInfo cbai = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO, 0, cp, VK_COMMAND_BUFFER_LEVEL_PRIMARY, 1 };
  VkCommandBuffer cb; CK(vkAllocateCommandBuffers(dev, &cbai, &cb));
  VkCommandBufferBeginInfo cbbi = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
  CK(vkBeginCommandBuffer(cb, &cbbi));
  vkCmdBindPipeline(cb, VK_PIPELINE_BIND_POINT_COMPUTE, pipe);
  vkCmdBindDescriptorSets(cb, VK_PIPELINE_BIND_POINT_COMPUTE, pl, 0, 1, &ds, 0, 0);
  vkCmdDispatch(cb, (ncases + 63) / 64, 1, 1);
  CK(vkEndCommandBuffer(cb));
  VkSubmitInfo si = { VK_STRUCTURE_TYPE_SUBMIT_INFO, 0, 0, 0, 0, 1, &cb };
  CK(vkQueueSubmit(q, 1, &si, 0)); CK(vkQueueWaitIdle(q));
  FILE *f = fopen(argv[3], "wb"); fwrite(map[1], 1, outn, f); fclose(f);
  return 0;
}
